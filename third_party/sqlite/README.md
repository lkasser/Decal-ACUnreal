# SQLite, 64-bit

`win-x64/sqlite3.dll` is SQLite 3.53.4 as sqlite.org ships it:
`https://www.sqlite.org/2026/sqlite-dll-win-x64-3530400.zip`, whose SHA3-256 is
`deddee963c810d1eeac3ce5e15c7c41da21a1c54d7a39cf54fbf577d2f50de3a` as the download page gives it.
SQLite is in the public domain.

Why it is here: Virindi Chat System 5, Integrator2 and Global Inventory keep their data in SQLite
through a `System.Data.SQLite.dll` that is IL-only and calls only SQLite's standard functions, and
ship a 32-bit `sqlite3.dll`, which this 64-bit host cannot load. Decal.Compat lays this one out as
`plugins\Decal.Compat\native\sqlite3.dll` and gives it to a plugin in place of its own
(`DecalPluginLoadContext.ResolveNative`). It exports everything that wrapper calls but
`sqlite3_key`, which only an encrypted database needs. The wrapper itself (1.0.61) reads a blob
with 32-bit pointer arithmetic, which each plugin's working copy has widened
(`src/Decal.Compat/PointerRewrite.cs`).
