/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module. 

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.DotNet;

namespace dnSpy.MCP.Server.Application {
	internal sealed class AliasRewriteResult {
		public string RewrittenExpression { get; set; } = string.Empty;
		public bool UsedAliases { get; set; }
		public string? Error { get; set; }
		public List<string> Notes { get; } = new List<string>();
	}

	internal sealed class DebuggerExpressionAliasContext {
		public string AssemblyName { get; set; } = string.Empty;
		public string TypeExpression { get; set; } = string.Empty;
		public TypeDef DeclaringType { get; set; } = null!;
		public bool HasInstanceThis { get; set; }

		public static DebuggerExpressionAliasContext Create(MethodDef method) => new DebuggerExpressionAliasContext {
			AssemblyName = method.Module.Assembly?.Name.String ?? string.Empty,
			TypeExpression = BuildTypeExpression(method.DeclaringType, method.Module.Assembly?.Name.String ?? string.Empty),
			DeclaringType = method.DeclaringType,
			HasInstanceThis = !method.IsStatic
		};

		internal static string BuildTypeExpression(TypeDef type, string assemblyName) {
			var fullName = type.FullName.Replace("/", "+");
			return $"global::System.Type.GetType(\"{Escape(fullName)}, {Escape(assemblyName)}\", false)";
		}

		internal static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	internal static class DebuggerExpressionAliasHelper {
		static readonly Regex ArgAliasRegex = new Regex(@"\$arg(?<index>\d+)|\barg\((?<index2>\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		static readonly Regex LocalAliasRegex = new Regex(@"\$local(?<index>\d+)|\blocal\((?<index2>\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		static readonly Regex FieldAliasRegex = new Regex(@"\bfield\(\s*""(?<name>[^""]+)""\s*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		static readonly Regex MemberByTokenRegex = new Regex(@"\bmemberByToken\(\s*""(?<token>[^""]+)""\s*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal) {
			"abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
			"continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
			"false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
			"internal","is","lock","long","namespace","new","null","object","operator","out","override","params",
			"private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc",
			"static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
			"unsafe","ushort","using","virtual","void","volatile","while"
		};

		public static AliasRewriteResult Rewrite(string expression, IReadOnlyList<string?> parameterNames, IReadOnlyCollection<int> localIndexes, DebuggerExpressionAliasContext? context = null) {
			var result = new AliasRewriteResult {
				RewrittenExpression = expression ?? string.Empty
			};

			result.RewrittenExpression = ArgAliasRegex.Replace(result.RewrittenExpression, match => {
				result.UsedAliases = true;
				var indexText = match.Groups["index"].Success ? match.Groups["index"].Value : match.Groups["index2"].Value;
				if (!int.TryParse(indexText, out var index) || index < 0 || index >= parameterNames.Count) {
					result.Error = $"Argument alias '{match.Value}' is out of range. Available args: 0..{Math.Max(0, parameterNames.Count - 1)}.";
					return match.Value;
				}

				var parameterName = parameterNames[index];
				if (string.IsNullOrWhiteSpace(parameterName)) {
					result.Error = $"Argument alias '{match.Value}' cannot be resolved because parameter {index} has no usable name in metadata.";
					return match.Value;
				}

				if (!TryGetCSharpExpressionName(parameterName!, out var expressionName)) {
					result.Error = $"Argument alias '{match.Value}' maps to parameter '{parameterName}', which is not a valid C# identifier for the dnSpy evaluator.";
					return match.Value;
				}

				if (!string.Equals(match.Value, expressionName, StringComparison.Ordinal))
					result.Notes.Add($"Resolved {match.Value} -> {expressionName}");
				return expressionName;
			});

			if (result.Error != null)
				return result;

			result.RewrittenExpression = FieldAliasRegex.Replace(result.RewrittenExpression, match => {
				result.UsedAliases = true;
				if (context == null) {
					result.Error = $"Field helper '{match.Value}' requires a managed method context.";
					return match.Value;
				}
				var fieldName = match.Groups["name"].Value;
				var field = context.DeclaringType.Fields.FirstOrDefault(f => string.Equals(f.Name.String, fieldName, StringComparison.Ordinal));
				if (field == null) {
					result.Error = $"Field helper '{match.Value}' could not resolve field '{fieldName}' in {context.DeclaringType.FullName}.";
					return match.Value;
				}

				if (!field.IsStatic && !context.HasInstanceThis) {
					result.Error = $"Field helper '{match.Value}' resolved an instance field, but the current method is static and has no 'this' reference.";
					return match.Value;
				}

				var reflectionExpr = BuildFieldValueExpression(context, field);
				result.Notes.Add($"Resolved {match.Value} -> reflection field access for token 0x{field.MDToken.Raw:X8}");
				return reflectionExpr;
			});

			if (result.Error != null)
				return result;

			result.RewrittenExpression = MemberByTokenRegex.Replace(result.RewrittenExpression, match => {
				result.UsedAliases = true;
				if (context == null) {
					result.Error = $"memberByToken helper '{match.Value}' requires a managed method context.";
					return match.Value;
				}

				if (!TryParseToken(match.Groups["token"].Value, out var token)) {
					result.Error = $"memberByToken helper '{match.Value}' contains an invalid metadata token.";
					return match.Value;
				}

				if (!TryResolveMember(context.DeclaringType.Module, token, out var replacement, out var note)) {
					result.Error = $"memberByToken helper '{match.Value}' could not resolve token 0x{token:X8} in module {context.DeclaringType.Module.Name}.";
					return match.Value;
				}

				result.Notes.Add(note);
				return replacement;
			});

			if (result.Error != null)
				return result;

			result.RewrittenExpression = LocalAliasRegex.Replace(result.RewrittenExpression, match => {
				result.UsedAliases = true;
				var indexText = match.Groups["index"].Success ? match.Groups["index"].Value : match.Groups["index2"].Value;
				if (!int.TryParse(indexText, out var index) || index < 0 || !localIndexes.Contains(index)) {
					var maxIndex = localIndexes.Count == 0 ? -1 : localIndexes.Max();
					result.Error = $"Local alias '{match.Value}' is out of range. Available locals: 0..{Math.Max(0, maxIndex)}.";
					return match.Value;
				}

				var expressionName = $"V_{index}";
				if (!string.Equals(match.Value, expressionName, StringComparison.Ordinal))
					result.Notes.Add($"Resolved {match.Value} -> {expressionName}");
				return expressionName;
			});

			return result;
		}

		static bool TryGetCSharpExpressionName(string name, out string expressionName) {
			expressionName = string.Empty;
			if (string.IsNullOrWhiteSpace(name))
				return false;

			if (!IsIdentifierStart(name[0]))
				return false;

			for (int i = 1; i < name.Length; i++) {
				if (!IsIdentifierPart(name[i]))
					return false;
			}

			expressionName = CSharpKeywords.Contains(name) ? "@" + name : name;
			return true;
		}

		static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);
		static bool IsIdentifierPart(char ch) => ch == '_' || char.IsLetterOrDigit(ch);

		static bool TryParseToken(string text, out uint token) {
			text = text?.Trim() ?? string.Empty;
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return uint.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out token);
			return uint.TryParse(text, out token);
		}

		static string BuildFieldValueExpression(DebuggerExpressionAliasContext context, FieldDef field) {
			const string flags = "global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic";
			var target = field.IsStatic ? "null" : "this";
			return $"{context.TypeExpression}.GetField(\"{DebuggerExpressionAliasContext.Escape(field.Name.String)}\", {flags}).GetValue({target})";
		}

		static bool TryResolveMember(ModuleDef module, uint token, out string replacement, out string note) {
			replacement = string.Empty;
			note = string.Empty;

			var field = module.GetTypes().SelectMany(t => t.Fields).FirstOrDefault(f => f.MDToken.Raw == token);
			if (field != null) {
				var context = new DebuggerExpressionAliasContext {
					AssemblyName = field.Module.Assembly?.Name.String ?? string.Empty,
					TypeExpression = DebuggerExpressionAliasContext.BuildTypeExpression(field.DeclaringType, field.Module.Assembly?.Name.String ?? string.Empty),
					DeclaringType = field.DeclaringType,
					HasInstanceThis = true
				};
				replacement = BuildFieldValueExpression(context, field);
				note = $"Resolved memberByToken(\"0x{token:X8}\") -> field value for {field.FullName}";
				return true;
			}

			var property = module.GetTypes().SelectMany(t => t.Properties).FirstOrDefault(p => p.MDToken.Raw == token);
			if (property != null) {
				const string flags = "global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic";
				var typeExpr = DebuggerExpressionAliasContext.BuildTypeExpression(property.DeclaringType, property.Module.Assembly?.Name.String ?? string.Empty);
				replacement = $"{typeExpr}.GetProperty(\"{DebuggerExpressionAliasContext.Escape(property.Name.String)}\", {flags})";
				note = $"Resolved memberByToken(\"0x{token:X8}\") -> PropertyInfo for {property.FullName}";
				return true;
			}

			var method = module.GetTypes().SelectMany(t => t.Methods).FirstOrDefault(m => m.MDToken.Raw == token);
			if (method != null) {
				const string flags = "global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic";
				var typeExpr = DebuggerExpressionAliasContext.BuildTypeExpression(method.DeclaringType, method.Module.Assembly?.Name.String ?? string.Empty);
				replacement = $"{typeExpr}.GetMethod(\"{DebuggerExpressionAliasContext.Escape(method.Name.String)}\", {flags})";
				note = $"Resolved memberByToken(\"0x{token:X8}\") -> MethodInfo for {method.FullName}";
				return true;
			}

			var type = module.GetTypes().FirstOrDefault(t => t.MDToken.Raw == token);
			if (type != null) {
				replacement = DebuggerExpressionAliasContext.BuildTypeExpression(type, type.Module.Assembly?.Name.String ?? string.Empty);
				note = $"Resolved memberByToken(\"0x{token:X8}\") -> Type for {type.FullName}";
				return true;
			}

			return false;
		}
	}
}
