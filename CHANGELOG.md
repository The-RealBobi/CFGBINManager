# CFGBINManager changelog

## v1.0.6

- Added reading, writing and reconstruction of 64-bit T2B values, with an explicit width option for ambiguous files.
- Preserved T2B name hash variants, value widths and padding through binary and CFG round trips.
- Rejected merges between T2B files with incompatible value widths or name hash variants.
- Fixed the hierarchy of Victory Road event commands and arguments while preserving the original declared counts.
- Expanded the Victory Road catalog with field names and parent-child relationships for dialogue, subtitles, models and time-zone tables.

## v1.0.5

- Updated the package and installation instructions to version 1.0.5 for .NET 8, .NET 9 and .NET 10.
