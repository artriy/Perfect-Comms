# Starlight Managed Dependency Notices

This file covers the managed runtime dependency closure merged into
`PerfectCommsStarlight.dll`. Its contents, together with the Perfect Comms
license and the complete SIPSorcery license, are embedded in the final DLL.
Package versions are the resolved versions in
`Starlight/packages.media.lock.json` and
`Starlight/packages.plugin.lock.json`. The repository-root native dependency
notices are not a substitute for these terms.

The two Microsoft abstractions are supplied by the Starlight host rather than
merged into `PerfectCommsStarlight.dll`. They remain part of the managed
runtime closure and are listed here.

## Dependency inventory

| NuGet package | Version | Runtime assembly | Project URL | Declared license |
| --- | --- | --- | --- | --- |
| BouncyCastle.Cryptography | 2.7.0 | `BouncyCastle.Cryptography.dll` | https://www.bouncycastle.org/stable/nuget/csharp/website | MIT |
| Common.Logging | 3.4.1 | `Common.Logging.dll` | http://net-commons.github.io/common-logging/ | Apache License 2.0 |
| Common.Logging.Core | 3.4.1 | `Common.Logging.Core.dll` | http://net-commons.github.io/common-logging/ | Apache License 2.0 |
| Concentus | 2.2.2 | `Concentus.dll` | https://github.com/lostromb/concentus | Package `LICENSE`, reproduced below |
| DnsClient | 1.8.0 | `DnsClient.dll` | http://dnsclient.michaco.net/ | Apache License 2.0 |
| IPNetwork2 | 2.1.2 | `System.Net.IPNetwork.dll` | https://github.com/lduchosal/ipnetwork | BSD 2-Clause, reproduced below |
| Makaretu.Dns | 2.0.1 | `Makaretu.Dns.dll` | https://github.com/richardschneider/net-dns | MIT |
| Makaretu.Dns.Multicast | 0.27.0 | `Makaretu.Dns.Multicast.dll` | https://github.com/richardschneider/net-mdns | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.11 | Host-provided `Microsoft.Extensions.DependencyInjection.Abstractions.dll` | https://dot.net/ | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 | Host-provided `Microsoft.Extensions.Logging.Abstractions.dll` | https://dot.net/ | MIT |
| SimpleBase | 1.3.1 | `SimpleBase.dll` | https://github.com/ssg/SimpleBase | Apache License 2.0 |
| SIPSorcery | 10.0.16 | `SIPSorcery.dll` | https://github.com/sipsorcery-org/sipsorcery/tree/master/src/SIPSorcery | Package `LICENSE.md`, reproduced verbatim in `SIPSorcery-LICENSE.md` |
| SIPSorcery.WebSocketSharp | 0.0.1 | `websocket-sharp.dll` | https://github.com/sipsorcery/websocket-sharp | MIT |
| SIPSorceryMedia.Abstractions | 10.0.16 | `SIPSorceryMedia.Abstractions.dll` | https://github.com/sipsorcery-org/sipsorcery/tree/master/src/SIPSorceryMedia.Abstractions | Package `LICENSE.md`, reproduced verbatim in `SIPSorcery-LICENSE.md` |

## SIPSorcery package terms

The `SIPSorcery` 10.0.16 and `SIPSorceryMedia.Abstractions` 10.0.16 packages contain identical `LICENSE.md` files. Those files are not plain BSD licenses. They contain a BSD 3-Clause license, an additional use restriction that takes precedence where it conflicts with the BSD terms, and the GNU Lesser General Public License v2.1 text for SIPSorceryMedia.FFmpeg. The complete package file is embedded without modification in `PerfectCommsStarlight.dll`; its repository source is `SIPSorcery-LICENSE.md`.

## MIT licensed components

The following notices apply to the MIT licensed dependencies:

- BouncyCastle.Cryptography 2.7.0: Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. Canonical package license: https://github.com/bcgit/bc-csharp/blob/4007498b13582d90ee1eda5d9920c324428b98b3/LICENSE.md
- Makaretu.Dns 2.0.1: Copyright (c) 2018 Richard Schneider. Canonical license: https://github.com/richardschneider/net-dns/blob/master/LICENSE
- Makaretu.Dns.Multicast 0.27.0: Copyright (c) 2018 Richard Schneider. Canonical license: https://github.com/richardschneider/net-mdns/blob/master/LICENSE
- Microsoft.Extensions.DependencyInjection.Abstractions 10.0.11 and Microsoft.Extensions.Logging.Abstractions 10.0.11: package metadata copyright Microsoft Corporation; license notice copyright (c) .NET Foundation and Contributors. Canonical license: https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT
- SIPSorcery.WebSocketSharp 0.0.1: Copyright (c) 2010-2019 sta.blockhead. Canonical license: https://github.com/sipsorcery/websocket-sharp/blob/master/LICENSE.txt

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Apache License 2.0 components

- Common.Logging 3.4.1 and Common.Logging.Core 3.4.1, by Aleksandar Seovic, Mark Pollack, Erich Eichinger, Stephen Bohlen, and contributors. Project license: https://github.com/net-commons/common-logging/blob/master/license.txt
- DnsClient 1.8.0: Copyright (c) 2024 Michael Conrad. Package-declared license: https://licenses.nuget.org/Apache-2.0; project license: https://github.com/MichaCo/DnsClient.NET/blob/f1e7ca33d713dc3bc70e7b3664aa3a2b6c090d5d/LICENSE
- SimpleBase 1.3.1: Copyright 2014-2017 Sedat Kapanoglu. Package license reference: http://www.apache.org/licenses/; canonical Apache License 2.0 text: https://www.apache.org/licenses/LICENSE-2.0

The complete Apache License 2.0 terms are available at https://www.apache.org/licenses/LICENSE-2.0.

## Concentus 2.2.2 package LICENSE

Copyright (c) by various holding parties, including (but not limited to):
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
  names of specific contributors, may be used to endorse or promote
  products derived from this software without specific prior written
  permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.

## IPNetwork2 2.1.2 license

Copyright (c) 2015, lduchosal
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
