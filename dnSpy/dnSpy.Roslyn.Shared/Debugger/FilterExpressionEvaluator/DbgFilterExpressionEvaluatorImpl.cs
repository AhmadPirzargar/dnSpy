﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code.FilterExpressionEvaluator;
using dnSpy.Roslyn.Shared.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace dnSpy.Roslyn.Shared.Debugger.FilterExpressionEvaluator {
	[ExportDbgManagerStartListener]
	sealed class ClearCompiledExpressions : IDbgManagerStartListener {
		readonly DbgFilterExpressionEvaluatorImpl dbgFilterExpressionEvaluatorImpl;
		[ImportingConstructor]
		ClearCompiledExpressions(DbgFilterExpressionEvaluatorImpl dbgFilterExpressionEvaluatorImpl) => this.dbgFilterExpressionEvaluatorImpl = dbgFilterExpressionEvaluatorImpl;
		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) => dbgManager.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
		void DbgManager_IsDebuggingChanged(object sender, EventArgs e) {
			var dbgManager = (DbgManager)sender;
			dbgFilterExpressionEvaluatorImpl.OnIsDebuggingChanged(dbgManager.IsDebugging);
		}
	}

	[ExportDbgFilterExpressionEvaluator(double.PositiveInfinity)]
	[Export(typeof(DbgFilterExpressionEvaluatorImpl))]
	sealed class DbgFilterExpressionEvaluatorImpl : DbgFilterExpressionEvaluator {
		readonly object lockObj;
		Dictionary<string, CompiledExpr> toCompiledExpr;
		WeakReference toCompiledExprWeakRef;

		sealed class CompiledExpr {
			public EvalDelegate Eval { get; }
			public string CompilationError { get; }
			public string RuntimeError { get; set; }
			public CompiledExpr(EvalDelegate eval) => Eval = eval ?? throw new ArgumentNullException(nameof(eval));
			public CompiledExpr(string compilationError) => CompilationError = compilationError ?? throw new ArgumentNullException(nameof(compilationError));
		}

		[ImportingConstructor]
		DbgFilterExpressionEvaluatorImpl() {
			lockObj = new object();
			toCompiledExpr = CreateCompiledExprDict();
		}

		static Dictionary<string, CompiledExpr> CreateCompiledExprDict() => new Dictionary<string, CompiledExpr>(StringComparer.Ordinal);

		internal void OnIsDebuggingChanged(bool isDebugging) {
			lock (lockObj) {
				// Keep the compiled expressions if possible (eg. user presses Restart button)
				if (isDebugging) {
					toCompiledExpr = toCompiledExprWeakRef?.Target as Dictionary<string, CompiledExpr> ?? toCompiledExpr ?? CreateCompiledExprDict();
					toCompiledExprWeakRef = null;
				}
				else {
					toCompiledExprWeakRef = new WeakReference(toCompiledExpr);
					toCompiledExpr = CreateCompiledExprDict();
				}
			}
		}

		public override string IsValidExpression(string expr) {
			if (expr == null)
				throw new ArgumentNullException(nameof(expr));
			lock (lockObj) {
				if (toCompiledExpr.TryGetValue(expr, out var compiledExpr))
					return compiledExpr.CompilationError;
			}
			return Compile(expr, verifyExpr: true).error;
		}

		public override DbgFilterExpressionEvaluatorResult Evaluate(string expr, DbgFilterEEVariableProvider variableProvider) {
			if (expr == null)
				throw new ArgumentNullException(nameof(expr));
			if (variableProvider == null)
				throw new ArgumentNullException(nameof(variableProvider));
			var compiledExpr = GetOrCompile(expr);
			if (compiledExpr.CompilationError != null)
				return new DbgFilterExpressionEvaluatorResult(compiledExpr.CompilationError);
			if (compiledExpr.RuntimeError != null)
				return new DbgFilterExpressionEvaluatorResult(compiledExpr.RuntimeError);

			bool evalResult;
			try {
				evalResult = compiledExpr.Eval(variableProvider.MachineName, variableProvider.ProcessId, variableProvider.ProcessName, variableProvider.ThreadId, variableProvider.ThreadName);
			}
			catch (Exception ex) {
				compiledExpr.RuntimeError = string.Format(dnSpy_Roslyn_Shared_Resources.FilterExpressionEvaluator_CompiledExpressionThrewAnException, ex.GetType().FullName);
				return new DbgFilterExpressionEvaluatorResult(compiledExpr.RuntimeError);
			}
			return new DbgFilterExpressionEvaluatorResult(evalResult);
		}

		CompiledExpr GetOrCompile(string expr) {
			lock (lockObj) {
				if (toCompiledExpr.TryGetValue(expr, out var compiledExpr))
					return compiledExpr;
				compiledExpr = CreateCompiledExpr(expr);
				toCompiledExpr.Add(expr, compiledExpr);
				return compiledExpr;
			}
		}

		CompiledExpr CreateCompiledExpr(string expr) {
			var compRes = Compile(expr);
			if (compRes.error != null)
				return new CompiledExpr(compRes.error);

			try {
				using (var delCreator = new EvalDelegateCreator(compRes.assembly, FilterExpressionClassName, EvalMethodName)) {
					var del = delCreator.CreateDelegate();
					if (del != null)
						return new CompiledExpr(del);
				}
			}
			catch (EvalDelegateCreatorException) {
			}
			return new CompiledExpr(dnSpy_Roslyn_Shared_Resources.FilterExpressionEvaluator_InvalidExpression);
		}

		const string FilterExpressionClassName = "FilterExpressionClass";
		const string EvalMethodName = "__EVAL__";
		static readonly CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
		static readonly CSharpParseOptions parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		static readonly SyntaxTree mscorlibSyntaxTree = CSharpSyntaxTree.ParseText(@"
namespace System {
	public class Object { }
	public abstract class ValueType { }
	public struct Void { }
	public struct Boolean { }
	public struct Int32 { }
	public sealed class String {
		public static bool operator ==(String left, String right) => false;
		public static bool operator !=(String left, String right) => false;
	}
}
", parseOptions);

		(byte[] assembly, string error) Compile(string expr, bool verifyExpr = false) {
			var filterExprClass = CSharpSyntaxTree.ParseText(@"
static class " + FilterExpressionClassName + @" {
	public static bool " + EvalMethodName + @"(string MachineName, int ProcessId, string ProcessName, int ThreadId, string ThreadName) =>
#line 1
" + expr + @";
}
", parseOptions);
			var comp = CSharpCompilation.Create("filter-expr-eval", new[] { mscorlibSyntaxTree, filterExprClass }, options: compilationOptions);
			var peStream = new MemoryStream();
			var emitResult = comp.Emit(peStream);
			if (!emitResult.Success) {
				var error = emitResult.Diagnostics.FirstOrDefault(a => a.Severity == DiagnosticSeverity.Error)?.ToString() ?? "Unknown error";
				return (null, error);
			}
			if (verifyExpr)
				return (null, null);
			return (peStream.ToArray(), null);
		}
	}
}
