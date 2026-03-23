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
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application {
	[Export(typeof(SourceMapTools))]
	public sealed class SourceMapTools {
		readonly IDocumentTreeView documentTreeView;
		readonly Dictionary<string, Dictionary<(string MapType, string OriginalFullName), string>?> loadedMaps =
			new Dictionary<string, Dictionary<(string MapType, string OriginalFullName), string>?>(StringComparer.OrdinalIgnoreCase);

		[ImportingConstructor]
		public SourceMapTools(IDocumentTreeView documentTreeView) {
			this.documentTreeView = documentTreeView;
		}

		static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
			WriteIndented = true,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		static string SourceMapsDir {
			get {
				var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				return Path.Combine(appData, "dnSpy", "dnSpy.MCPServer", "sourcemaps");
			}
		}

		public CallToolResult GetSourceMapName(Dictionary<string, object>? arguments) {
			var (assembly, member) = ResolveTarget(arguments, requireMember: false);
			var effectiveMember = GetDefToMap(member);
			var mappedName = GetMappedName(assembly, effectiveMember);

			var json = JsonSerializer.Serialize(new {
				Assembly = assembly.FullName,
				Target = DescribeMember(effectiveMember),
				MappedName = mappedName,
				CachePath = GetCachePath(assembly)
			}, JsonOpts);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		public CallToolResult SetSourceMapName(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("mapped_name", out var mappedNameObj))
				throw new ArgumentException("mapped_name is required");

			var mappedName = mappedNameObj?.ToString()?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(mappedName))
				throw new ArgumentException("mapped_name must not be empty");

			var (assembly, member) = ResolveTarget(arguments, requireMember: false);
			var effectiveMember = GetDefToMap(member);

			var map = EnsureLoadedMap(assembly, loadFromCacheIfPresent: true);
			map[(GetMapType(effectiveMember), effectiveMember.FullName)] = mappedName;
			SaveMapTo(assembly, GetCachePath(assembly));

			var json = JsonSerializer.Serialize(new {
				Assembly = assembly.FullName,
				Target = DescribeMember(effectiveMember),
				MappedName = mappedName,
				CachePath = GetCachePath(assembly),
				Note = "SourceMap updated in memory and saved to cache."
			}, JsonOpts);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		public CallToolResult ListSourceMapEntries(Dictionary<string, object>? arguments) {
			var assembly = RequireAssembly(arguments);
			var map = EnsureLoadedMap(assembly, loadFromCacheIfPresent: true);

			var entries = map
				.OrderBy(k => k.Key.MapType, StringComparer.Ordinal)
				.ThenBy(k => k.Key.OriginalFullName, StringComparer.Ordinal)
				.Select(k => new {
					MapType = k.Key.MapType,
					OriginalFullName = k.Key.OriginalFullName,
					MappedName = k.Value
				})
				.ToList();

			var json = JsonSerializer.Serialize(new {
				Assembly = assembly.FullName,
				EntryCount = entries.Count,
				Entries = entries,
				CachePath = GetCachePath(assembly)
			}, JsonOpts);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		public CallToolResult SaveSourceMap(Dictionary<string, object>? arguments) {
			var assembly = RequireAssembly(arguments);
			var outputPath = arguments != null && arguments.TryGetValue("output_path", out var outputPathObj)
				? outputPathObj?.ToString()
				: null;
			var path = string.IsNullOrWhiteSpace(outputPath) ? GetCachePath(assembly) : outputPath!;

			SaveMapTo(assembly, path);

			var json = JsonSerializer.Serialize(new {
				Assembly = assembly.FullName,
				OutputPath = path,
				EntryCount = EnsureLoadedMap(assembly, loadFromCacheIfPresent: true).Count
			}, JsonOpts);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		public CallToolResult LoadSourceMap(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("input_path", out var inputPathObj))
				throw new ArgumentException("input_path is required");

			var inputPath = inputPathObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(inputPath))
				throw new ArgumentException("input_path must not be empty");
			if (!File.Exists(inputPath))
				throw new ArgumentException($"SourceMap file not found: {inputPath}");

			var assembly = RequireAssembly(arguments);
			var loaded = LoadMapFrom(assembly, inputPath);
			var cachePath = GetCachePath(assembly);
			if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(cachePath), StringComparison.OrdinalIgnoreCase))
				SaveMapTo(assembly, cachePath);

			var json = JsonSerializer.Serialize(new {
				Assembly = assembly.FullName,
				InputPath = inputPath,
				CachePath = cachePath,
				EntryCount = loaded.Count
			}, JsonOpts);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		AssemblyDef RequireAssembly(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("assembly_name", out var asmObj))
				throw new ArgumentException("assembly_name is required");
			var assembly = FindAssemblyByName(asmObj?.ToString() ?? string.Empty);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmObj}");
			return assembly;
		}

		(AssemblyDef assembly, IMemberDef member) ResolveTarget(Dictionary<string, object>? arguments, bool requireMember) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");

			var assembly = RequireAssembly(arguments);
			if (!arguments.TryGetValue("type_full_name", out var typeObj))
				throw new ArgumentException("type_full_name is required");

			var type = FindTypeInAssembly(assembly, typeObj?.ToString() ?? string.Empty);
			if (type == null)
				throw new ArgumentException($"Type not found: {typeObj}");

			var memberKind = arguments.TryGetValue("member_kind", out var kindObj)
				? (kindObj?.ToString() ?? string.Empty).Trim().ToLowerInvariant()
				: string.Empty;
			var memberName = arguments.TryGetValue("member_name", out var memberObj)
				? memberObj?.ToString()
				: null;

			if (string.IsNullOrWhiteSpace(memberKind) && string.IsNullOrWhiteSpace(memberName))
				return (assembly, type);

			if (string.IsNullOrWhiteSpace(memberKind))
				throw new ArgumentException("member_kind is required when member_name is provided");
			if (string.IsNullOrWhiteSpace(memberName))
				throw new ArgumentException("member_name is required when member_kind is provided");

			IMemberDef? member = memberKind switch {
				"type" => type,
				"method" => type.Methods.FirstOrDefault(m => m.Name.String == memberName),
				"field" => type.Fields.FirstOrDefault(f => f.Name.String == memberName),
				"property" => type.Properties.FirstOrDefault(p => p.Name.String == memberName),
				_ => throw new ArgumentException($"Unsupported member_kind '{memberKind}'. Use type, method, field, or property.")
			};

			if (member == null)
				throw new ArgumentException($"{memberKind} '{memberName}' not found in {type.FullName}");

			return (assembly, member);
		}

		string? GetMappedName(AssemblyDef assembly, IMemberDef member) {
			var map = EnsureLoadedMap(assembly, loadFromCacheIfPresent: true);
			var key = (GetMapType(member), member.FullName);
			if (map.TryGetValue(key, out var mappedName))
				return mappedName;

			if (member is MethodDef method) {
				if (method.HasImplMap)
					return method.ImplMap?.Name;
				if (method.HasOverrides)
					return method.Overrides.First().MethodDeclaration.Name;
			}

			return null;
		}

		Dictionary<(string MapType, string OriginalFullName), string> EnsureLoadedMap(AssemblyDef assembly, bool loadFromCacheIfPresent) {
			var key = assembly.FullName;
			if (loadedMaps.TryGetValue(key, out var cached))
				return cached ?? new Dictionary<(string MapType, string OriginalFullName), string>();

			if (loadFromCacheIfPresent && File.Exists(GetCachePath(assembly)))
				return LoadMapFrom(assembly, GetCachePath(assembly));

			var map = new Dictionary<(string MapType, string OriginalFullName), string>();
			loadedMaps[key] = map;
			return map;
		}

		Dictionary<(string MapType, string OriginalFullName), string> LoadMapFrom(AssemblyDef assembly, string path) {
			var map = new Dictionary<(string MapType, string OriginalFullName), string>();
			using var reader = XmlReader.Create(path);
			while (reader.Read()) {
				if (!reader.IsStartElement())
					continue;

				var type = reader.Name;
				if (!IsSupportedMapType(type))
					continue;

				var original = reader["original"];
				var mapped = reader["mapped"];
				if (original == null || mapped == null)
					continue;
				if (string.Equals(reader.GetAttribute("encoding"), "base64", StringComparison.OrdinalIgnoreCase))
					original = Encoding.UTF8.GetString(Convert.FromBase64String(original));
				map[(type, original)] = mapped;
			}

			loadedMaps[assembly.FullName] = map;
			return map;
		}

		void SaveMapTo(AssemblyDef assembly, string path) {
			var map = EnsureLoadedMap(assembly, loadFromCacheIfPresent: true);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
			writer.WriteStartDocument();
			writer.WriteStartElement("SourceMap");
			foreach (var entry in map.OrderBy(x => x.Key.MapType, StringComparer.Ordinal).ThenBy(x => x.Key.OriginalFullName, StringComparer.Ordinal)) {
				writer.WriteStartElement(entry.Key.MapType);
				if (entry.Key.OriginalFullName.Any(ch => ch < ' ')) {
					writer.WriteAttributeString("encoding", "base64");
					writer.WriteAttributeString("original", Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Key.OriginalFullName)));
				}
				else {
					writer.WriteAttributeString("original", entry.Key.OriginalFullName);
				}
				writer.WriteAttributeString("mapped", entry.Value);
				writer.WriteEndElement();
			}
			writer.WriteEndElement();
			writer.WriteEndDocument();
		}

		static string GetMapType(IMemberDef member) => member switch {
			MethodDef _ => "MethodDef",
			TypeDef _ => "TypeDef",
			FieldDef _ => "FieldDef",
			PropertyDef _ => "PropertyDef",
			_ => "Other"
		};

		static bool IsSupportedMapType(string type) =>
			type == "MethodDef" || type == "TypeDef" || type == "FieldDef" || type == "PropertyDef" || type == "Other";

		static IMemberDef GetDefToMap(IMemberDef def) {
			if (def is MethodDef method && method.IsConstructor)
				return def.DeclaringType;
			return def;
		}

		static string DescribeMember(IMemberDef member) => member switch {
			TypeDef type => $"{type.FullName} [type]",
			MethodDef method => $"{method.FullName} [method]",
			FieldDef field => $"{field.FullName} [field]",
			PropertyDef property => $"{property.FullName} [property]",
			_ => member.FullName
		};

		string GetCachePath(AssemblyDef assembly) {
			Directory.CreateDirectory(SourceMapsDir);
			var invalid = Path.GetInvalidFileNameChars();
			var fileName = new string(assembly.FullName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
			return Path.Combine(SourceMapsDir, fileName + ".xml");
		}

		AssemblyDef? FindAssemblyByName(string name) {
			return documentTreeView.GetAllModuleNodes()
				.Select(node => node.Document?.AssemblyDef)
				.FirstOrDefault(assembly => assembly != null && assembly.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
		}

		static TypeDef? FindTypeInAssembly(AssemblyDef assembly, string typeFullName) {
			foreach (var module in assembly.Modules) {
				foreach (var type in module.GetTypes()) {
					if (type.FullName == typeFullName)
						return type;
				}
			}
			return null;
		}
	}
}
