# Third-party notices

MCPIndexSearch is built on the packages pinned in `Directory.Packages.props` and their locked transitive dependencies. Distributions must retain the licenses shipped by those packages and downloaded model assets.

- IBM Granite Embedding 311M Multilingual R2: Apache 2.0 model materials at revision `44399559930365213510b1ee2eb15ded83374f0e`. Its tokenizer is derived from the Gemma 3 tokenizer and is subject to the Gemma Terms of Use; acceptance is recorded by the in-app or command-line model installer.
- ONNX Runtime: Microsoft, MIT License. Hardware-dependent quantization behavior is documented by the ONNX Runtime project.
- PP-OCRv6 medium detector (`PaddlePaddle/PP-OCRv6_medium_det_onnx`): Apache License 2.0, revision `61323801669c338b7891481ec7bac61ce31b576a`, model SHA-256 `eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1`.
- PP-OCRv6 medium multilingual recognizer (`PaddlePaddle/PP-OCRv6_medium_rec_onnx`): Apache License 2.0, revision `50c7eacafc52fa7bcf4194e8cd08e46f8558504b`, model SHA-256 `9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba`, configuration/dictionary SHA-256 `991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129`.
- PDFium binaries distributed through PDFtoImage and its transitive packages: Chromium/PDFium licenses and included third-party notices apply.
- SkiaSharp / Skia and HarfBuzzSharp / HarfBuzz: MIT/BSD-style licenses and upstream third-party notices apply.
- PdfPig: Apache License 2.0.
- DocumentFormat.OpenXml: MIT License.
- MimeKit: MIT License.
- MsgReader: upstream license supplied with the package; OpenMcdf: MIT License.
- AngleSharp and Markdig: MIT License.
- Avalonia UI, CommunityToolkit.Mvvm, Microsoft.Extensions.*, Microsoft.Data.Sqlite, and ModelContextProtocol C# SDK: licenses supplied with their packages (principally MIT).
- SQLite/FTS5 and SQLitePCLRaw native packaging: SQLite public-domain dedication and package-specific license notices apply.
- BitMiracle.LibTiff.NET / libtiff: BSD-style libtiff license.
- Serilog and its sinks/extensions: Apache License 2.0.
- Tokenizers.HuggingFace and its native tokenizer dependencies: licenses supplied with the locked packages.

This notice is a distribution aid, not a substitute for the complete license texts contained in NuGet packages and downloaded upstream assets.
