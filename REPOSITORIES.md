# Repositories and versions

- `The-RealBobi/CFGBINManager_prv`: private development source, verification tools and research notes.
- `The-RealBobi/CFGBINManager`: public runtime source, catalog, build scripts and integration documentation.

`Directory.Build.props` is the authoritative version source in both checkouts. Runtime files are identical; the public repository starts its own history and never merges private history. `public-files.txt` is the explicit production allowlist. Dumps, diagnostics, verification fixtures, plans, caches and packages are excluded.

Initialize or clone the public checkout separately, then run:

```sh
python3 scripts/sync-public.py /path/to/public/CFGBINManager
bash scripts/sync-version.sh 0.2.6 /path/to/private/CFGBINManager /path/to/public/CFGBINManager
```

On Windows the equivalent version command is `scripts/sync-version.ps1`. Synchronization copies and compares selected files without committing or pushing. It rejects unrelated tracked public files rather than deleting them. Inspect both Git diffs before committing or publishing. Build packages with `build.sh` or `build.ps1`; both use the same project and place output in `dist/`.
