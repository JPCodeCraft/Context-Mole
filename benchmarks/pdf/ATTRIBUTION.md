# Dataset attribution and evaluation references

## olmOCR-bench

- Creator: Allen Institute for AI (Ai2), olmOCR team.
- Source: https://huggingface.co/datasets/allenai/olmOCR-bench
- Dataset revision: `54a96a6fb6a2bd3b297e59869491db4d3625b711`.
- Dataset license: Open Data Commons Attribution License 1.0 (ODC-BY-1.0):
  https://opendatacommons.org/licenses/by/1-0/ . The pinned dataset card and each
  original check's source URL are retained in the external cache.
- Evaluation specification: https://github.com/allenai/olmocr/tree/f7cfe4c22098b154c76b6ec950d1c0a464eecf8d/olmocr/bench
- Upstream evaluator code is Apache-2.0. The local evaluator is independently
  implemented and documents adaptations; it does not vendor upstream Python,
  RapidFuzz, or KaTeX code and does not claim official score equivalence.

The dataset card describes 7,010 checks. The pinned JSONLs have 7,019 rows because
they also include nine baseline checks. Original PDFs and check annotations remain
unmodified; normalized cache manifests are derived metadata.

## ViDoRe v3 HR

- Creator: Illuin Technology, ViDoRe team.
- Source: https://huggingface.co/datasets/vidore/vidore_v3_hr
- Dataset revision: `0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3`.
- Annotations, relevance judgments and generated metadata: Creative Commons
  Attribution 4.0 International (CC BY 4.0): https://creativecommons.org/licenses/by/4.0/ .
- Original PDF and supplied OCR text licenses are inherited from their publishers,
  independently of the annotations' license. The pinned `documents_metadata`
  reports `cc-by-4.0` for each of the 14 original PDFs. Each document's specific
  license and publication URL are retained in both source metadata and the derived
  manifest; this is not a transfer of corpus ownership to the annotation authors.
- The source PDFs are European Union publications. The two smoke documents are
  *A demographic perspective on the future of European labour markets*
  (`a_demographic_perspective_on_the_future_of_european-KJ0125152ENN`) and
  *Employment and social developments in Europe*
  (`employment_and_social_developments_in_europe-KE0125067ENN`). Their exact
  publisher URLs are in the manifest.
- Evaluation code reference: https://github.com/illuin-tech/vidore-benchmark/tree/a70f23af8bb3b33efe8a4a6c6c15a6e2d978035e .
- The dataset card's historical end-to-end evaluation reference
  `95f2f83a5a09590a89e34960479f9438e48bca77` is recorded separately; it is not
  presented as a verified commit of the evaluator-code repository.

Derived manifests convert zero-based physical page numbers to one-based PDF pages
and pixel rectangles to normalized displayed-page rectangles using each corpus
image's width/height. They omit supplied OCR Markdown. The retrieval evaluator
scores page-deduplicated consumer search output; it is an adaptation of the visual
retrieval protocol and does not produce or grade LLM answers.
