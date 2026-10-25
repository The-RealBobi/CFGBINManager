# CFGBINManager v1.0.2

## Add it to your project

Use a .NET 10 project and install [CFGBINManager from NuGet](https://www.nuget.org/packages/CFGBINManager):

```sh
dotnet add package CFGBINManager --version 1.0.2
```

To use the source directly, add this to your `.csproj`, adjusting the relative path:

```xml
<ItemGroup>
  <ProjectReference Include="../CFGBINManager/src/CFGBINManager/CFGBINManager.csproj" />
</ItemGroup>
```

## Load a file

```csharp
using CFGBINManager;

string catalogPath = Path.Combine(AppContext.BaseDirectory, "data", "structure.cfg");
var catalog = StructureCatalog.Parse(File.ReadAllText(catalogPath))
    .ForProfile("VictoryRoad");

var document = BinCodec.Read(File.ReadAllBytes("input.cfg.bin"), catalog);
```

Choose the game name in your catalog. For a catalog without game profiles, omit `.ForProfile(...)`.

To open a CFG instead, replace the last line with:

```csharp
var document = CfgTextCodec.Read(File.ReadAllText("input.cfg"), catalog);
```

The original BIN is not needed. A plain text CFG can be loaded directly as T2B. For an exported RDBNP CFG, retain the reconstruction section included in the text file.

## Read and change a value

```csharp
var rows = document.FindRows("CHARA_BASE_INFO");
var row = rows[0]; // Choose an existing row.

string? name = row.GetValue("internalName").AsString();
row.SetValue("internalName", CfgValue.String("example_name"));
```

Use the entry and field names from your file. You can also address a field by position, keeping its existing value type:

```csharp
var value = row.Values[0];
row.SetValue(0, CfgValue.Signed(123));
```

## Save the result

```csharp
File.WriteAllBytes("edited.cfg.bin", BinCodec.Write(document));
File.WriteAllText("edited.cfg", CfgTextCodec.WriteReadable(document));
```

Use a different output filename to keep your input intact.

## Work without files

```csharp
string cfg = CfgTextCodec.WriteReadable(document);
var imported = CfgTextCodec.Read(cfg, catalog);
byte[] bin = BinCodec.Write(imported);
```

[GNU General Public License v3.0](LICENSE).
