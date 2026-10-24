# CFGBINManager v1.0.0

Managed .NET API for loading, querying and editing Level-5 T2B and RDBNP record files, independent of VictoryTool. Targets .NET 10 on Windows and macOS, including Apple Silicon. No UI, native library or external NuGet dependency.

## Integrate

Reference `src/CFGBINManager/CFGBINManager.csproj` from another .NET project, or install the local package generated in `dist/`. The package includes `data/structure.cfg` as content copied to the application's output directory. A caller can also supply the catalog entirely in memory.

```csharp
using CFGBINManager;

var catalog = StructureCatalog.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "data", "structure.cfg")));

CfgDocument document = BinCodec.Read(File.ReadAllBytes(inputPath), catalog);
IReadOnlyList<CfgRow> rows = document.FindRows("CharacterBody");
string? modelPath = rows[0].GetValue("bodyModelPath").AsString();
rows[0].SetValue("bodyModelPath", CfgValue.String("models/example.g4md"));

// The same operations work entirely in memory, without an intermediate file.
string cfg = CfgTextCodec.Write(document);
CfgDocument imported = CfgTextCodec.Read(cfg, catalog);
byte[] result = BinCodec.Write(imported);
```

The codecs have no filesystem side effects. Write `result` to a separate destination or use your application's existing atomic-save mechanism. Parse and validate before replacing a source file.

### Values and schemas

`CfgValue` distinguishes signed and unsigned integers, floats with their exact bits, hashes, booleans, strings, vectors, arrays, nested structures, range references and opaque T2B type-3 words. `AsBoolean`, `AsInt64`, `AsUInt64`, `AsSingle`, `AsDouble`, `AsString` and `AsRange` check their type. Null strings and empty strings remain distinct. Embedded NUL is rejected.

Rows expose ordered `Values` and `FieldNames`. Array, nested structure and vector components are in `CfgValue.Items`; replace a composite with a new value of the same kind when editing it. A Vector3 includes its fourth stored word as an unsigned component. `document.Types` exposes RDBNP type names, hashes, layout IDs, primitive/nested flags, element sizes, array counts and both stored and expanded physical field offsets. `document.GetListFields(name)` includes primitive-root descriptors as well as structured roots. `CfgField.PrimitiveSize` reports the native primitive width independently of padded storage/array stride (`ElementSize`). `BinaryVersion` and `IsBigEndian` expose the native profile.

`FindRows` accepts a native type/entry name or an unambiguous catalog alias and preserves source order and duplicates. T2B fields can always be queried by positional strings such as `"0"`; RDBNP fields use their native names. RDBNP root lists are available through `GetList(nativeListName)`.

### Insert and remove

```csharp
CfgList list = document.GetList("m_ScenarioSoccerDemoDataList");
CfgRow template = list.Rows[0];
CfgRow added = template.Clone();
document.InsertListRow(list.Name, list.Rows.Count, added);

// For T2B, use document.InsertRow(position, newRow) and RemoveRow(position).
```

Use `Clone()` to retain a template row's physical padding and value shapes, or create a new `CfgRow` for an entirely new record. Create a new row object for each insertion. Its values must match the existing native schema. Root schemas and layout IDs cannot be changed through row edits.

The API handles these maintenance operations:

- Structural header counts, section offsets, fixed-array storage and string offsets are rebuilt on every binary write.
- Declared T2B `COUNT` fields are calculated on CFG export and binary write, without modifying the input model during serialization.
- Declared T2B ranges, individual row indices and RDBNP REF16/REF32 ranges are rebased by insertion/removal methods. Insertion inside a range, including its first element, expands it; insertion at its end leaves it unchanged. Deletion within a range shrinks it. Removing a row with an incoming individual-index reference is rejected until that reference is explicitly changed.
- RDBNP auxiliary indices retain their original order when unchanged. Key or row edits rebuild a stable order using `INDEX_KEY` or a uniquely consistent key inferred from the original permutation. An ambiguous key requires an explicit declaration before editing.
- Unknown nonempty RDBNP reference bindings block structural edits, since the API cannot safely choose which list they target. Scalar edits and unchanged reconstruction remain available.

Physical row order, duplicates and descriptor order are retained. An auxiliary index is a separate lookup order; it does not reorder the rows themselves. The library does not infer game policies from filenames or names containing `BEGIN`/`COUNT`.

Create an empty UTF-8 T2B document with `CfgDocument.CreateT2b(catalog)`, then create/insert records with `CreateT2bRow` / `InsertRow`. RDBNP creation requires a native schema from an existing document.

## `data/structure.cfg`

CFGBINManager uses its own catalog syntax: `GAME` profiles containing typed record blocks and ordinary CFG field declarations. It does not adopt the other program's bracket/parenthesis syntax or its unexplained `True/False` markers.

```text
GAME "VictoryRoad" {
    T2B "CHARA_BODY_INFO", "CharacterBody" {
        "bodyModelId";
        "skeletonModelPath";
        "bodyModelPath";
        "motionFamilyCrc32";
    }

    RDBNP "SCENARIO_SOCCER_DEMO_INFO", "ScenarioDemo" {
        "userTeam", "team";
        "assignData";
    }

    COUNT "T2B", "CHARA_BODY_INFO_LIST_BEG", 0, "CHARA_BODY_INFO", "CHARA_BODY_INFO_LIST_END";
    REFERENCE "SCENARIO_SOCCER_DEMO_INFO", "assignData", "m_ScenarioSoccerDemoDataList";
    INDEX_KEY "m_MenuDataFileConfigList", "character_id";
    LAYOUT_FIELD "SCENARIO_SOCCER_DEMO_DATA", 0x5273E9D1, "opponentList", 28;
}
```

Put each field declaration and closing brace on its own line. Header values, field names and aliases use CFG string quoting/escaping. `//` comments are allowed outside strings.

- `GAME "name" { ... }` selects a game's definitions. The bundled catalog contains only the confirmed `VictoryRoad` profile.
- `T2B "nativeName", "optionalAlias" { ... }` declares ordered positional field labels. `RDBNP` declares native named members instead. Typing each record distinguishes names present in both formats.
- A field declaration contains its label and optional alias: `"name";` or `"nativeName", "friendlyName";`. Optional aliases never replace native hashes or spellings.
- T2B field order supplies the numeric position. Repeated display labels remain ordered; an ambiguous label query raises an error. Numeric queries keep their native positional meaning, including when an external definition uses numbers as display labels.
- RDBNP labels bind by native descriptor name; catalog order never replaces binary member order. The binary supplies the actual field types and physical record schema.
- `UnkN` names unresolved positions. They are placeholders for meaning, not guesses at a type. The supplied samples fill observed T2B positions; revisions may contain fewer or additional values. Remaining values are always available by index.
- Native T2B labels are scoped to the exact number of fields declared in their record block. Row queries apply this automatically; a different row shape remains available by numeric position. `GetFields(format, entry, valueCount)` and `ResolveField(format, entry, field, valueCount)` expose the same contextual lookup. The existing overloads without `valueCount` inspect the declared catalog and do not infer a row variant. Legacy `FIELD` declarations have no arity and retain their original behavior.
- Empty record blocks mean that field labels are unknown; they do not enforce zero-value rows. Binary types, hash presentation and references are not inferred from a label or an unexplained external flag.

A one-game catalog is selected automatically, so existing consumers still use `StructureCatalog.Parse(text)`. With multiple games, select one explicitly:

```csharp
StructureCatalog catalog = StructureCatalog.Parse(schemaText).ForProfile("VictoryRoad");
IReadOnlyList<string> profiles = catalog.ProfileNames;
IReadOnlyList<StructureField> fields = catalog.GetFields(BinaryFormat.T2b, "CharacterBody", rows[0].Values.Count);
```

The previous v0.1.0 `STRUCTURE_VERSION` / `ENTRY` / `FIELD` declaration format remains readable for compatibility. The bundled file now uses the native format above. Maintenance relations remain explicit declarations inside the game:

```text
RANGE "T2B", "CHARA_MOTION_PRESET_INFO_REF_DATA", 0, 1, "CHARA_MOTION_PRESET_DATA";
ROW_INDEX "T2B", "CHARA_BASE_INFO_REF_BATTLE", 0, "CHARA_BASE_BATTLE", 0;
ROW_INDEX "T2B", "CHARA_PARTS_CLOTHES_MODEL_REF_INFO", 0, "CHARA_PARTS_CLOTHES_INFO", null;
```

An empty closing marker in `COUNT` explicitly means EOF, as in the old CPK table; repeated openings are rejected as ambiguous. `ROW_INDEX` takes an absent sentinel as its last argument, or `null` when every index, including zero, is real. `LAYOUT_FIELD` is qualified by the native layout identifier. Adding labels, aliases or confirmed maintenance declarations requires no code change.

The supplied catalog retains all 2,780 T2B entry names, 549 RDBNP types, 90 previously confirmed T2B meanings and 2,764 RDBNP member identities. It also names the 16 confirmed counters and retains the range relation, three individual-index relations, four RDBNP reference bindings, explicit index key and six physical layout overrides. Unknown meanings are not guessed. Unverified game schemas from the supplied syntax example are not bundled as game support.

The catalog includes the researched character, item and resource labels while retaining former `UnkN` aliases. There are 5,244 unresolved T2B labels. Labels identify known data meanings; gameplay rules such as legality remain the consumer's responsibility.

Editable display names can be stored separately from canonical names and aliases:

```text
LABEL "T2B", "CHARA_BASE_INFO", 34, "0", "Character ID";
```

Place LABEL declarations inside their GAME block (or beside legacy declarations). They are scoped by native record name, value count and a dot-separated numeric component path. `SetFieldLabels` validates a batch before applying it; `GetFieldLabel` resolves a display name; `ToText()` serializes the catalog including every profile, existing aliases, maintenance rules and edited labels. Display names do not change binary identities or the aliases used by row queries.

`SetChildRules` replaces a selected game's explicit CHILD relationships after validating distinct names and rejecting cycles; `ToText` preserves these edits along with all aliases, profiles and maintenance rules. This supports authored, name-based containment without embedding a fake parent pointer in T2B records.

`MoveT2bRows(start, count, destination)` and `MoveListRows(list, start, count, destination)` move existing rows transactionally, with the destination expressed in the original order. Declared row references follow their target identities. Referenced ranges must remain contiguous; unsupported auxiliary indices are rejected. Writers recalculate declared counts. `GetBlockEnd` reports declared or conventional _BEG/_BGN/_BEGIN/_START closing names; `GetRecordTree` projects matching conventional pairs as well as declared scopes. Projection does not guess a missing counter field.

## CFG interchange and exact reconstruction

Content retains the readable DUMP syntax:

```text
CHARA_BODY_INFO 100, "skeleton.g4sk", "body.g4md", 123456;
```

Lossless exports also contain `// @...` metadata statements. They preserve the native descriptors, per-row type shapes, exact string references, ordered physical string/name pools, padding and indices. Structured metadata is JSON inside a quoted CFG string; byte arrays describe opaque regions only. Floating NaN payloads use hexadecimal bit literals. There is no embedded original BIN or unmodified-file shortcut, and ordinary row payloads are not duplicated in metadata.

`CfgTextCodec.WriteReadable(document)` emits an alternative full-document CFG: one value per line with field-name/type comments, followed by gzip/base64 reconstruction metadata. `Read` accepts both forms. The readable writer does not duplicate row payloads in its footer. Keep the header and footer; values can be edited directly, while structural edits (record identity/order/count) must be made through the API/editor before exporting again. Decompressed metadata is limited to 256 MiB. Leading whitespace/comments do not disable reconstruction.

Retain those comments when editing text if byte identity matters. Every write allocates a new buffer and reconstructs records. Changing a shared string in one field appends/reuses the appropriate pool string without altering other references. Unused and duplicated physical strings survive unchanged reconstruction.

Plain T2B CFG without metadata is also importable and gets a deterministic native layout. RDBNP CFG requires the exported native schema/list metadata because aliases alone cannot describe its physical records. Removing metadata forfeits original layout identity. Malformed input, incompatible values, missing reference targets and unsupported layouts produce explicit exceptions.

## Build and package

Install the .NET 10 SDK, then run `./build.sh` on macOS or `./build.ps1` on Windows. Both build the library and package the API into `dist/`. Verification tools and research evidence live in the private development repository and are not required to build the public source.

See [repository workflow](REPOSITORIES.md) for public/private synchronization and shared versioning.

### Evidence and limits

The v0.1.0 binary-codec verification inspected **169,939 T2B and 697 RDBNP files** across revisions 7.1.2, 7.2.1 and 1.0.0. Exact reconstruction of unchanged records is checked through fresh CFG import. Synthetic support for big-endian RDBNP, 64-bit primitives/hash64/double, REF32 and UInt32 auxiliary indices is separate from real-game evidence; those cases were absent from the corpus.

The implemented profiles are native four-byte T2B values and RDBNP version 100. T2B code 3 is an opaque four-byte word; RDBNP code 12 is unregistered and rejected. Layout-ID generation and arbitrary schema replacement are not implemented. Nested fields use the observed four-byte packing rule, with explicit catalog offsets available for verified exceptions; layouts violating bounds or overlapping fields are rejected. A structurally plausible unseen packing rule cannot be established from corpus roundtrips alone.

Actual execution by the game, Windows runtime execution and consumer-specific VictoryTool integration are separate from these format checks. VictoryTool's application policies and writers remain in VictoryTool; this package provides the standalone format API for consumers to reference.

Historical reconstruction evidence is separate from the current package build. Detailed per-revision validation records remain in the private development repository; a successful build alone does not establish byte-for-byte equivalence or game-runtime correctness.


### Consumer provenance and fixed string allocations

`CfgDocument.T2bInfo` exposes source string-pool bounds and encoding; `CfgRow.T2bInfo` exposes source value offsets, raw words and name candidates for binary-loaded T2B records. Newly created, cloned or CFG-imported rows may have no source provenance. `CfgList.TypeName` identifies an RDBNP list's native type even when the list is empty. These properties do not retain an original BIN buffer.

`BinCodec.WriteWithFixedStringEdits(document, edits)` reconstructs T2B before applying bounded encoded replacements, pads unused allocation bytes with spaces, and rejects shared or overlapping suffix references. It preserves the caller document and rejects null strings, NUL text and growth beyond the allocation. Ordinary editable strings should use row edits followed by `BinCodec.Write`.

## Record containment

`document.GetRecordTree()` returns read-only `CfgRecordNode` roots. Each node has `Row`, `ListName`, `RowIndex` and `Children`. T2B nodes refer to source rows and use the selected catalog's COUNT start/end scopes; closing records remain the final child and empty-ended scopes extend to EOF. RDBNP list containers have a null Row and RowIndex -1; their children refer to list-local row indices. The projection never reorders or mutates the document. Recompute it after structural changes. Unknown rows retain their physical location; conventional matched boundary suffixes are display hints, and ID references do not imply ownership. Unmatched, crossed or conflicting declared block boundaries throw InvalidDataException. This tree validation is stricter than independent COUNT recalculation because a tree requires properly nested scopes.

Contiguous companion records can be declared inside the game profile with `CHILD "T2B", "PARENT_RECORD", "CHILD_RECORD";`. A child attaches only to an active preceding parent in the same block, while successive declared children continue that branch; unrelated siblings close it. Orphan companions remain at their physical location. This is display containment, not a deletion/copy cascade or an index reference. The bundled catalog declares CHARA_BASE_INFO_REF_BATTLE as a child of its preceding CHARA_BASE_INFO based on the existing native loader evidence.

## Explicit document edits

T2B consumers can use CreateT2bRow, SetT2bValue, InsertT2bValue and RemoveT2bValue without manipulating opaque layout objects. These operations validate native physical kinds, 32-bit limits, NUL strings and the 255-value row limit. Existing SetValue keeps its same-kind contract. Serialize/reparse a cloned document before adopting risky structural edits; count/index declarations can reject an edit that changes a required layout.

ConvertTextEncoding(0/1) returns a reconstructed T2B document in Shift-JIS/UTF-8 without mutating its input. It re-encodes physical pools, rebuilds name offsets/hashes and clears stale string offsets. Unrepresentable text, nonstandard hashes and unresolved names fail explicitly. TextEncodingCode also works for CFG-loaded documents.

CfgTextCodec.WriteFragment(document, rows) emits selected physical rows with their typed CFG metadata and required native schema. It excludes original global string pools, whole-file opaque gaps and original index keys. A fragment is for import, not independent binary reconstruction. CloneForImport clears source string-pointer provenance, including nested strings. Reference-bearing records require a target mapping; single-row insertion is not an automatic cross-document reference remapper.

Conventional containment is a display inference: unmatched markers remain ordinary rows, without adding or removing binary records. Explicit Structure COUNT scopes remain strict.

## ConflictMerge API

`CfgMergePlan.GetKeyOptions(current, incoming)` lists unique scalar key candidates by native list and entry type. Select `MergeKey` values explicitly, then call `CfgMergePlan.Analyze(current, incoming, keys)`. An empty key selection matches repeated entries by exact subtree content, using occurrence queues so duplicates are not collapsed. A single entry of the same type in a parent is paired by role. Keys are scoped to siblings, never physical or display indices.

The plan exposes `Conflicts`, `MatchedRows`, `AddedRows` and `CurrentOnlyRows`. Every conflict must receive `MergeSide.Current` or `MergeSide.Incoming` in the dictionary supplied to `Build`. Counts are maintained by the writer; T2B ranges are chosen atomically. Declared T2B row indices/ranges and native RDBNP references are remapped to merged rows. Live native references without a target declaration and split/reordered ranges fail explicitly. Equal stable leaf reference destinations are compared by content without changing the reference maps.

The result retains the current document's ordering/layout, adds incoming-only branches before closing markers, and preserves current-only rows. There is no inferred deletion policy or three-way change detection: two files cannot establish which conflicting value was an intentional mod edit. T2B matched values must have compatible shapes; RDBNP requires the same native types and lists. Trees deeper than 128 blocks are rejected before recursive merge processing. Both sources and catalogs are snapshotted, and every Build returns an independently reconstructed and validated document.

`CfgMergePlan.Entries` exposes the planned hierarchy as read-only `MergeEntry` and `MergeField` snapshots. Fields retain both source values, conflict IDs and automatic counter previews for review interfaces. Closing entries remain in this projection so consumers can present every field.
