# Third-Party Notices

This file records the open-source runtime dependencies of `1.0.0-beta.3`, checked against the restored Desktop, Bridge, Add-In and tool package graphs and NuGet license metadata. EPLAN API DLLs, EPLAN documentation, SDK materials and user/vendor EDZ/MDB files are external prerequisites and are not redistributed.

| Component | Version | License | Homepage |
|---|---:|---|---|
| SharpCompress | 0.50.4 | MIT | https://github.com/adamhathcock/sharpcompress |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 8.0.31 | MIT | https://learn.microsoft.com/dotnet/standard/data/sqlite/ |
| SQLite | bundled through SQLitePCLRaw 2.1.12 | Public Domain | https://www.sqlite.org/copyright.html |
| SQLitePCLRaw.bundle_e_sqlite3 / core / provider / lib | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Newtonsoft.Json | 13.0.3 | MIT | https://www.newtonsoft.com/json |
| System.Memory | 4.6.3 (Bridge), 4.5.3 (Desktop transitive) | MIT | https://github.com/dotnet/maintenance-packages |
| System.Buffers | 4.6.1 | MIT | https://github.com/dotnet/maintenance-packages |
| System.Numerics.Vectors | 4.6.1 | MIT | https://github.com/dotnet/maintenance-packages |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | https://github.com/dotnet/runtime |
| System.Text.Encoding.CodePages | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Threading.Tasks.Extensions | 4.5.4 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| .NET 8 Windows Desktop Runtime (self-contained Desktop and Tools publish) | 8.0.30 | MIT plus Microsoft notices | https://dotnet.microsoft.com/ |
| Inno Setup installer engine | 6.7.3 | Inno Setup License | https://jrsoftware.org/isinfo.php |

## License texts and attribution

License texts and upstream notices are preserved under [docs/licenses/](docs/licenses/) and copied into `Docs/licenses/` in application packages:

- [SharpCompress MIT](docs/licenses/SharpCompress.MIT.txt): Adam Hathcock; exact package source revision.
- [Newtonsoft.Json MIT](docs/licenses/Newtonsoft.Json.MIT.txt): James Newton-King; package-supplied text.
- [SQLitePCLRaw Apache-2.0](docs/licenses/SQLitePCLRaw.Apache-2.0.txt): upstream v2.1.12 license, including bundle/core/provider/native wrapper licensing. SQLite itself remains public domain as stated by its upstream copyright page.
- [Microsoft/.NET MIT](docs/licenses/DotNet.MIT.txt), [Windows Desktop MIT](docs/licenses/WindowsDesktop.MIT.txt), and [.NET runtime third-party notices](docs/licenses/DotNet.THIRD-PARTY-NOTICES.txt).
- Legacy package notices for [System.Memory](docs/licenses/System.Memory.THIRD-PARTY-NOTICES.txt) and [System.Threading.Tasks.Extensions](docs/licenses/System.Threading.Tasks.Extensions.THIRD-PARTY-NOTICES.txt).
- [Inno Setup license](docs/licenses/InnoSetup.LICENSE.txt), supplied with the installed compiler. Inno Setup is a build dependency; its installer engine is included in Setup, not the application Portable ZIP runtime.

The legacy System.Memory 4.5.3 and System.Threading.Tasks.Extensions 4.5.4 packages supply MIT license files rather than a modern NuGet license-expression node. Those files were checked directly. Upstream copyright/author credits in the license texts are legal attribution, not the developer's personal contact details.

Test-only dependencies (Microsoft.NET.Test.Sdk, xUnit, test runner and coverage tooling) are restored for development, not shipped in application packages. Their upstream licenses continue to apply. Project-owned source uses the owner-selected MIT license in `LICENSE`; it does not relicense external proprietary software.

Review this inventory, version-specific package license files and notices again whenever a dependency or runtime version changes. No third-party license grant in this document authorizes redistribution of EPLAN or vendor/user data.

Inno Setup's compiler displays a non-commercial-use banner. Its [official commercial-license guidance](https://jrsoftware.org/isorder.php) requests commercial users to purchase a license, states that purchase is not strictly required, and distinguishes running generated installers from using the compiler. Review the upstream terms for your own build context; the project MIT license does not replace them.
