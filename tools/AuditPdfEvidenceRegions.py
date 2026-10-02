"""Post-hoc source-box overcoverage diagnostics; never changes retrieval scores.

Gold rectangles may be incomplete. Inherited source-block boxes cover more than
literal excerpts; normalized geometry cannot prove visual crop precision or answer
completeness. Precision here means overlap against supplied annotation rectangles.
"""
from __future__ import annotations
import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
import statistics


def valid(box):
    return len(box) == 4 and all(math.isfinite(x) for x in box) and 0 <= box[0] < box[2] <= 1 and 0 <= box[1] < box[3] <= 1


def intersection(a, b):
    result = [max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])]
    return result if result[0] < result[2] and result[1] < result[3] else None


def union_area(boxes):
    boxes = list(boxes)
    boundaries = sorted({x for box in boxes for x in (box[0], box[2])})
    result = 0.0
    for left, right in zip(boundaries, boundaries[1:]):
        intervals = sorted((box[1], box[3]) for box in boxes if box[0] < right and box[2] > left)
        height, start, end = 0.0, 0.0, 0.0
        for a, b in intervals:
            if a > end:
                height += end - start
                start, end = a, b
            else:
                end = max(end, b)
        result += (right - left) * (height + end - start)
    return result


def metric(query, corpus_pages, row, k):
    gold = defaultdict(list)
    for qrel in query['qrels']:
        gold[corpus_pages[qrel['corpus_id']]].extend(qrel['bounding_boxes'])
    top_pages = {(page['document_id'], page['page_number']) for page in row['ranked_pages'][:k]}
    predicted = defaultdict(list)
    invalid, no_geometry = 0, 0
    for evidence in row['exposed_evidence']:
        page = evidence['original_document_id'], evidence['page_number']
        if page not in top_pages:
            continue
        if not evidence['literal_citation_valid']:
            invalid += 1
            continue
        region = evidence.get('location', {}).get('region')
        if region is None:
            no_geometry += 1
            continue
        box = [region['x'], region['y'], region['x'] + region['width'], region['y'] + region['height']]
        if valid(box):
            predicted[page].append(box)
        else:
            no_geometry += 1
    predicted_area = sum(union_area(boxes) for boxes in predicted.values())
    relevant_predicted_area = sum(union_area(boxes) for page, boxes in predicted.items() if page in gold)
    gold_area = sum(union_area(boxes) for boxes in gold.values())
    overlap = sum(union_area(overlap for a in boxes for b in gold.get(page, []) if (overlap := intersection(a, b)))
                  for page, boxes in predicted.items())
    coverage = None if gold_area == 0 else overlap / gold_area
    official = next(m for m in row['metrics'] if m['k'] == k)['evidence_area_coverage']
    if official is not None and (coverage is None or abs(coverage - official) > 1e-8):
        raise ValueError('Post-hoc geometry projection disagrees with official report coverage; do not silently proceed.')
    return {'k': k, 'source_box_annotation_precision_all_top_k_pages': None if predicted_area == 0 else overlap / predicted_area,
            'source_box_annotation_precision_relevant_top_k_pages': None if relevant_predicted_area == 0 else overlap / relevant_predicted_area,
            'unannotated_fraction_of_predicted_area': None if predicted_area == 0 else (predicted_area - overlap) / predicted_area,
            'predicted_area_all_top_k_pages': predicted_area, 'predicted_area_relevant_top_k_pages': relevant_predicted_area,
            'predicted_area_irrelevant_top_k_pages': predicted_area - relevant_predicted_area,
            'overlap_area': overlap, 'gold_area_all_pages': gold_area, 'existing_evidence_area_coverage_reproduced': coverage,
            'top_k_invalid_literal_citations': invalid, 'top_k_valid_citations_without_valid_geometry': no_geometry}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True, type=Path)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    manifest = json.loads(args.manifest.read_text())
    queries = {str(query['id']): query for query in manifest['queries']}
    pages = {page['corpus_id']: (page['document_id'], page['page_number']) for page in manifest['pages']}
    rows = []
    for row in report['queries']:
        if 'exposed_evidence' not in row:
            raise ValueError('Report lacks --include-evidence output; precision cannot be inferred from page rankings alone.')
        rows.append({'id': row['id'], 'language': row['language'], 'preview_policy': row.get('preview_policy', 'rank'),
                     'metrics': [metric(queries[row['id']], pages, row, m['k']) for m in row['metrics']]})
    summaries = []
    for policy in sorted({row['preview_policy'] for row in rows}):
        for language in ['all'] + sorted({row['language'] for row in rows}):
            subset = [row for row in rows if row['preview_policy'] == policy and (language == 'all' or row['language'] == language)]
            for k in sorted({m['k'] for row in subset for m in row['metrics']}):
                metrics = [next(m for m in row['metrics'] if m['k'] == k) for row in subset]
                summary = {'policy': policy, 'language': language, 'k': k, 'queries': len(subset)}
                for key in metrics[0]:
                    if key == 'k':
                        continue
                    values = [m[key] for m in metrics if m[key] is not None]
                    summary[key] = statistics.mean(values) if values else None
                    if len(values) != len(metrics):
                        summary[key + '_available_queries'] = len(values)
                summaries.append(summary)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({'version': 1, 'post_hoc_diagnostic': True, 'changes_existing_metrics': False,
        'report': str(args.report), 'manifest': str(args.manifest),
        'limitations': ['Annotation rectangles can be incomplete; unannotated area is not necessarily wrong.',
                       'Source boxes are inherited full extraction-block bounds and can exceed literal excerpt bounds.',
                       'Normalized geometry is a source-location check; it does not prove crop precision, visual content, answer correctness or completeness.',
                       'All valid exposed regions on each selected top-K physical page are unioned, matching existing coverage semantics.'],
        'summary': summaries, 'queries': rows}, indent=2) + '\n')
    print(args.output)


if __name__ == '__main__':
    main()
