# Archive fixtures

`sharpcompress-rar4.rar` is the `Rar.rar` regression fixture from
[SharpCompress 0.50.4](https://github.com/adamhathcock/sharpcompress/tree/0.50.4/tests/TestArchives/Archives),
which is distributed under the repository's MIT license. The upstream license is retained in
[`THIRD-PARTY-LICENSES/SharpCompress.txt`](../../THIRD-PARTY-LICENSES/SharpCompress.txt). Its SHA-256 is
`60DB161DE57DC59AA12E0C45B1B70D78904DA3D104E74748972AC38643F12802`.

The fixture is intentionally kept in the repository because .NET does not include a RAR writer. It lets
`ArchiveSmoke.cs` exercise real RAR indexing and materialization without requiring WinRAR, 7-Zip, or a network
download at test time.
