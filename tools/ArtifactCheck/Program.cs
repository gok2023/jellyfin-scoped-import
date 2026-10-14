using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is not (1 or 2)) throw new ArgumentException("Usage: ArtifactCheck DLL [expected-assembly-version]");
using var file = File.OpenRead(args[0]);
using var pe = new PEReader(file);
var reader = pe.GetMetadataReader();
var assembly = reader.GetAssemblyDefinition();
var name = reader.GetString(assembly.Name);
var version = assembly.Version.ToString();
if (args.Length == 2 && version != args[1]) throw new InvalidDataException("Assembly version mismatch");
var resources = reader.ManifestResources.Select(h => reader.GetString(reader.GetManifestResource(h).Name)).Order().ToArray();
if (name == "ScopedImport" && !resources.SequenceEqual(new[] { "ScopedImport.app.html", "ScopedImport.import.html" }))
    throw new InvalidDataException("Embedded pages are missing or renamed");
var references = reader.AssemblyReferences.Select(h => {
    var a = reader.GetAssemblyReference(h);
    return new { name = reader.GetString(a.Name), version = a.Version.ToString() };
}).ToArray();
if (name == "ScopedImport" && references.Where(a => a.name.StartsWith("MediaBrowser.") || a.name == "Jellyfin.Data" || a.name == "Emby.Naming")
    .Any(a => a.version != "10.11.11.0")) throw new InvalidDataException("Unexpected Jellyfin ABI");
var debugPaths = pe.ReadDebugDirectory().Where(d => d.Type == DebugDirectoryEntryType.CodeView)
    .Select(d => pe.ReadCodeViewDebugDirectoryData(d).Path).ToArray();
if (debugPaths.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^[A-Za-z]:[\\/]|^\\\\")))
    throw new InvalidDataException("Private absolute path in PE debug metadata; configure PathMap");
Console.WriteLine(JsonSerializer.Serialize(new { name, version, resources, references, debugPaths,
    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant() }));
