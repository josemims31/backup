# 第三方组件与许可

Backup-fang523 自有部分使用 GPL-3.0-only，见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。以下第三方组件保持各自的原始许可；原始版权声明和许可文件均予保留。依赖 DLL 未作修改。

## 原生依赖

| 组件 | 随附版本 | 许可文件 | 上游与源码 |
| --- | --- | --- | --- |
| 7-Zip Extra / x64 7za.dll | 26.05 | [原始说明](licenses/7zip/License.txt)、[LGPL 2.1 全文](licenses/7zip/LGPL-2.1.txt)；DLL 为 LGPL-2.1-or-later | [26.05 官方发布](https://github.com/ip7z/7zip/releases/tag/26.05)、[随附对应源码](licenses/7zip/7z2605-src.tar.xz) |
| Everything SDK / Everything64.dll | 原仓库随附 DLL 未提供版本号 | [SDK 原始许可及 PCRE 声明](licenses/everything-sdk/LICENSE.txt)（MIT 及其中列出的 BSD 条款） | [SDK 官方页面](https://www.voidtools.com/support/everything/sdk/) |

7-Zip DLL 的 SHA-256：`7b3043136452637c58a01fbb0c6a6b9b0b1a749fba644c5968e7c21a539207e0`。已核对它与官方 `7z2605-extra.7z` 中的 `x64/7za.dll` 完全一致。随附的 `License.txt` 也与该官方包一致；此处使用的是 **7-Zip Extra 的许可说明**。

随附的 `7z2605-src.tar.xz` 是上游原始源码归档，包含上游构建说明。官方下载：[7z2605-src.tar.xz](https://github.com/ip7z/7zip/releases/download/26.05/7z2605-src.tar.xz)。SHA-256：`0b6080b8c3e7a579161d2d495224c0e51c467576cb4c7048b0ab485ef44cc22e`。程序动态加载 `7za.dll`，可以使用接口兼容的 DLL 替换它。

Everything 主程序不包含在此源码包中，使用 Everything 索引功能时需另行安装并运行。SDK 原始许可文件中的 PCRE 声明一并保留。

## SFTP 及其托管依赖

以下版本与 `work/tools/sshnet/packages.json` 一致。DLL 保留在原构建路径，构建无需在线还原 NuGet。各目录还保留了对应 NuGet 包中提供的 `THIRD-PARTY-NOTICES.TXT`（如果存在）。

| 组件 | 版本 | 许可 | 本地许可文件 | 来源 |
| --- | --- | --- | --- | --- |
| SSH.NET | 2026.0.0 | MIT | [license](licenses/ssh.net-2026.0.0/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/SSH.NET/2026.0.0), [source](https://github.com/sshnet/SSH.NET/tree/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e) |
| BouncyCastle.Cryptography | 2.7.0 | MIT | [license](licenses/bouncycastle.cryptography-2.7.0/LICENSE.md) | [NuGet](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0), [source](https://github.com/bcgit/bc-csharp/tree/4007498b13582d90ee1eda5d9920c324428b98b3) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | [license](licenses/microsoft.extensions.logging.abstractions-8.0.3/LICENSE.TXT) | [NuGet](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3), [source](https://github.com/dotnet/runtime/tree/eba546b0f0d448e0176a2222548fd7a2fbf464c0) |
| System.Formats.Asn1 | 10.0.10 | MIT | [license](licenses/system.formats.asn1-10.0.10/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/System.Formats.Asn1/10.0.10), [source](https://github.com/dotnet/dotnet/tree/f7d90799ce4ef09a0bb257852a57248d2a8fb8dd) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | [license](licenses/microsoft.extensions.dependencyinjection.abstractions-8.0.2/LICENSE.TXT) | [NuGet](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/8.0.2), [source](https://github.com/dotnet/runtime/tree/81cabf2857a01351e5ab578947c7403a5b128ad1) |
| System.Buffers | 4.6.1 | MIT | [license](licenses/system.buffers-4.6.1/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/System.Buffers/4.6.1), [source](https://github.com/dotnet/maintenance-packages/tree/6b84308c9ad012f53240d72c1d716d7e42546483) |
| System.Memory | 4.6.3 | MIT | [license](licenses/system.memory-4.6.3/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/System.Memory/4.6.3), [source](https://github.com/dotnet/maintenance-packages/tree/f62ca0009b038cab4725a720f386623a969d73ad) |
| System.ValueTuple | 4.5.0 | MIT | [license](licenses/system.valuetuple-4.5.0/LICENSE.TXT) | [NuGet](https://www.nuget.org/packages/System.ValueTuple/4.5.0), [package](https://www.nuget.org/packages/System.ValueTuple/4.5.0) |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | [license](licenses/microsoft.bcl.asyncinterfaces-8.0.0/LICENSE.TXT) | [NuGet](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces/8.0.0), [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) |
| System.Threading.Tasks.Extensions | 4.5.4 | MIT | [license](licenses/system.threading.tasks.extensions-4.5.4/LICENSE.TXT) | [NuGet](https://www.nuget.org/packages/System.Threading.Tasks.Extensions/4.5.4), [package](https://www.nuget.org/packages/System.Threading.Tasks.Extensions/4.5.4) |
| System.Numerics.Vectors | 4.6.1 | MIT | [license](licenses/system.numerics.vectors-4.6.1/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/System.Numerics.Vectors/4.6.1), [source](https://github.com/dotnet/maintenance-packages/tree/6b84308c9ad012f53240d72c1d716d7e42546483) |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | [license](licenses/system.runtime.compilerservices.unsafe-6.1.2/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe/6.1.2), [source](https://github.com/dotnet/maintenance-packages/tree/f62ca0009b038cab4725a720f386623a969d73ad) |
| Microsoft.Bcl.Cryptography | 10.0.10 | MIT | [license](licenses/microsoft.bcl.cryptography-10.0.10/LICENSE.txt) | [NuGet](https://www.nuget.org/packages/Microsoft.Bcl.Cryptography/10.0.10), [source](https://github.com/dotnet/dotnet/tree/f7d90799ce4ef09a0bb257852a57248d2a8fb8dd) |

对于 NuGet 包中未附带许可全文的组件，许可从包元数据所指向的源码仓库及对应提交提取：

- SSH.NET：`sshnet/SSH.NET`，提交 `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e` 的 `LICENSE`。
- System.Formats.Asn1、Microsoft.Bcl.Cryptography：`dotnet/dotnet`，提交 `f7d90799ce4ef09a0bb257852a57248d2a8fb8dd` 的 `LICENSE.TXT`。
- System.Buffers、System.Numerics.Vectors：`dotnet/maintenance-packages`，提交 `6b84308c9ad012f53240d72c1d716d7e42546483` 的 `LICENSE`。
- System.Memory、System.Runtime.CompilerServices.Unsafe：`dotnet/maintenance-packages`，提交 `f62ca0009b038cab4725a720f386623a969d73ad` 的 `LICENSE`。

原有 `work/tools/sshnet/THIRD-PARTY-SFTP.txt` 也保留，不以本清单替换原始声明。

## 构建和再分发

`build-ui.ps1` 会将项目的 `LICENSE`、`NOTICE`、本文件及整个 `licenses/` 目录复制到构建输出中，其中包含与 7-Zip DLL 对应的源码归档。原有的 `LICENSE-7ZIP.txt`、`LICENSE-EVERYTHING.txt` 及 SFTP 模块的许可说明仍会随构建输出提供。

发布编译好的软件时，请保留这些许可和版权说明，并提供所发布版本的项目源码及构建脚本。若替换第三方组件版本，应同时更新其许可、来源以及需要随附的对应源码。
