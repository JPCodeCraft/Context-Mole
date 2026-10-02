"""Compare fixed-input PDF reports with paired, cluster-resampled uncertainty.

This never changes the scorer or selects queries using observed quality. Extraction
resampling is by original PDF (checks are correlated); retrieval resampling is by
verified translation pair when --pairs points at a selection.json. A diversity
ablation can be compared within one report using --policy-a / --policy-b.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import random
import statistics


def intervals(values, clusters, repetitions=2000):
    groups = defaultdict(list)
    for identity, value in values.items():
        groups[clusters.get(identity, identity)].append(value)
    grouped = list(groups.values())
    if not grouped:
        return None
    rng = random.Random(20261001)
    observations = []
    for _ in range(repetitions):
        sample = [value for group in rng.choices(grouped, k=len(grouped)) for value in group]
        observations.append(statistics.mean(sample))
    observations.sort()
    return {"mean_delta": statistics.mean(values.values()), "paired_cluster_bootstrap_95_percent":
            [observations[int(.025 * (repetitions - 1))], observations[int(.975 * (repetitions - 1))]],
            "clusters": len(grouped), "resamples": repetitions, "seed": 20261001}


def retrieval(before, after, clusters):
    first = {str(row['id']): row for row in before}
    second = {str(row['id']): row for row in after}
    if len(first) != len(before) or len(second) != len(after):
        raise ValueError('Duplicate query IDs within a preview policy; comparison would silently discard rows.')
    if first.keys() != second.keys():
        raise ValueError('Query selections differ; a paired quality claim is invalid.')
    cutoffs = sorted({metric['k'] for row in before for metric in row['metrics']})
    result = []
    for k in cutoffs:
        for metric in ('recall', 'ndcg', 'reciprocal_rank', 'evidence_area_coverage', 'evidence_region_recall'):
            deltas, rows = {}, []
            for identity, row in first.items():
                if row['text'] != second[identity]['text'] or row['language'] != second[identity]['language']:
                    raise ValueError('Query text/language changed between compared variants.')
                original = next(m for m in row['metrics'] if m['k'] == k)[metric]
                candidate = next(m for m in second[identity]['metrics'] if m['k'] == k)[metric]
                if original is None or candidate is None:
                    continue
                delta = candidate - original
                deltas[identity] = delta
                rows.append({'id': identity, 'language': row['language'], 'text': row['text'],
                             'before': original, 'after': candidate, 'delta': delta})
            result.append({'k': k, 'metric': metric, 'query_count': len(rows),
                           'before_mean': statistics.mean(row['before'] for row in rows) if rows else None,
                           'after_mean': statistics.mean(row['after'] for row in rows) if rows else None,
                           'uncertainty': intervals(deltas, clusters),
                           'improved': sum(d > 1e-12 for d in deltas.values()), 'regressed': sum(d < -1e-12 for d in deltas.values()),
                           'unchanged': sum(abs(d) <= 1e-12 for d in deltas.values()),
                           'by_language': {language: {'count': len(subset), 'before': statistics.mean(r['before'] for r in subset),
                                'after': statistics.mean(r['after'] for r in subset), 'delta': statistics.mean(r['delta'] for r in subset)}
                                for language in sorted({row['language'] for row in rows})
                                if (subset := [row for row in rows if row['language'] == language])},
                           'worst_regressions': sorted((row for row in rows if row['delta'] < -1e-12), key=lambda row: (row['delta'], row['id']))[:10]})
    return result


def policy_rows(report, requested):
    policy = requested or report.get('primary_preview_policy', 'rank')
    rows = [row for row in report['queries'] if row.get('preview_policy', 'rank') == policy]
    if not rows:
        raise ValueError(f'Requested preview policy {policy!r} is absent from the report.')
    if len({str(row['id']) for row in rows}) != len(rows):
        raise ValueError(f'Duplicate query IDs in preview policy {policy!r}.')
    return policy, rows


def validate_manifest_selection(before, after, baseline_path, candidate_path):
    manifests = []
    for report, path in ((before, baseline_path), (after, candidate_path)):
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != report.get('manifest_sha256'):
            raise ValueError('Provided manifest hash differs from its original report.')
        manifests.append(json.loads(data))
    first, second = manifests
    for name in ('dataset', 'revision', 'coordinates_frame'):
        if first[name] != second[name]:
            raise ValueError('Manifest datasets/revisions/coordinate frames differ.')
    for name, identity in (('documents', 'id'), ('pages', 'corpus_id')):
        left = {row[identity]: row for row in first[name]}
        right = {row[identity]: row for row in second[name]}
        if len(left) != len(first[name]) or len(right) != len(second[name]) or left != right:
            raise ValueError('Manifest original documents or complete physical page corpus differ.')
    left = {str(row['id']): row for row in first['queries']}
    right = {str(row['id']): row for row in second['queries']}
    for identity in left.keys() & right.keys():
        if left[identity] != right[identity]:
            raise ValueError('Shared manifest query text, relevance or annotations differ.')
    if not left.keys() <= right.keys() and not right.keys() <= left.keys():
        raise ValueError('Manifests must be an explicit full/partition subset of identical questions.')
    return {'identical_complete_corpus_verified': True, 'shared_questions_and_qrels_verified': True,
            'baseline_manifest': str(baseline_path), 'candidate_manifest': str(candidate_path),
            'baseline_query_count': len(left), 'candidate_query_count': len(right)}


def validate_configuration(before, after, allow_ocr_change=False, allow_model_change=False, verified_manifest_selection=False):
    changes = {}
    first_identity = before.get('document_identity_protocol', 'generated-v7')
    second_identity = after.get('document_identity_protocol', 'generated-v7')
    if first_identity != second_identity:
        raise ValueError('Benchmark document identity protocols differ.')
    if first_identity != 'generated-v7':
        def identity_map(report):
            rows = report.get('document_identity_map')
            if not rows or len({row['original_id'] for row in rows}) != len(rows):
                raise ValueError('Stable benchmark document identity map is absent or duplicated.')
            return {row['original_id']: (row['source_sha256'], row['document_id']) for row in rows}
        if identity_map(before) != identity_map(after):
            raise ValueError('Stable document IDs or verified source hashes differ.')
    for name in ('manifest_sha256', 'manifestSha256', 'mode', 'ocr_enabled', 'selected_model', 'candidate_limit', 'result_options', 'threads'):
        if before.get(name) == after.get(name):
            continue
        allowed = name == 'ocr_enabled' and allow_ocr_change or name == 'selected_model' and allow_model_change or name == 'manifest_sha256' and verified_manifest_selection
        if not allowed:
            raise ValueError(f'Comparison input/config differs: {name}.')
        changes[name] = {'before': before.get(name), 'after': after.get(name)}
    if allow_model_change:
        first, second = before.get('embedding_policy') or {}, after.get('embedding_policy') or {}
        if not before.get('selected_model') or not after.get('selected_model'):
            raise ValueError('A controlled model comparison requires both selected models.')
        for name in ('precision', 'dimensions'):
            if first.get(name) != second.get(name):
                raise ValueError(f'Model-only comparison must retain the same output {name}; label precision/dimension experiments separately.')
        changes['embedding_policy'] = {'before': first, 'after': second}
    return changes


def extraction(before, after):
    first = {row['id']: row for row in before['evaluation']['checks']}
    second = {row['id']: row for row in after['evaluation']['checks']}
    if first.keys() != second.keys():
        raise ValueError('Extraction check identities differ; scorer selection cannot change between variants.')
    categories = defaultdict(list)
    transitions = Counter()
    for identity, row in first.items():
        candidate = second[identity]
        if any(row[key] != candidate[key] for key in ('pdf', 'page', 'category', 'type')):
            raise ValueError('Check identity metadata changed.')
        categories[row['category']].append({'id': identity, 'pdf': row['pdf'], 'before': row['status'], 'after': candidate['status']})
        transitions[row['status'] + '->' + candidate['status']] += 1
    return {'transitions': dict(sorted(transitions.items())), 'before': {key: value for key, value in before['evaluation'].items() if key != 'checks'},
            'after': {key: value for key, value in after['evaluation'].items() if key != 'checks'},
            'categories': {category: {'gained': [r for r in rows if r['before'] != 'passed' and r['after'] == 'passed'],
                                     'lost': [r for r in rows if r['before'] == 'passed' and r['after'] != 'passed'],
                                     'conservative_pass_delta': intervals({r['id']: float(r['after'] == 'passed') - float(r['before'] == 'passed') for r in rows}, {r['id']: r['pdf'] for r in rows})}
                           for category, rows in sorted(categories.items())}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--policy-a', help='Explicit baseline policy; defaults to report primary policy (rank for legacy reports)')
    parser.add_argument('--policy-b', help='Explicit candidate policy; defaults to report primary policy, or diverse for same-file ablation')
    parser.add_argument('--pairs', type=Path)
    parser.add_argument('--selection', type=Path, help='Filter full-corpus retrieval reports to every query in a locked partition manifest')
    parser.add_argument('--allow-ocr-change', action='store_true', help='Explicit controlled OCR on/off ablation; disclose the changed configuration')
    parser.add_argument('--allow-model-change', action='store_true', help='Explicit controlled model comparison; require matching precision/output dimensions and disclose policies')
    parser.add_argument('--baseline-manifest', type=Path, help='Verify an explicitly different full/partition selection against its original report hash')
    parser.add_argument('--candidate-manifest', type=Path, help='Use with --baseline-manifest; complete documents/pages and shared query/qrels must be identical')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    before = json.loads(args.baseline.read_text())
    after = json.loads(args.candidate.read_text()) if args.candidate else before
    manifest_verification = None
    if bool(args.baseline_manifest) != bool(args.candidate_manifest):
        raise ValueError('Both original manifest paths are required for explicit full/partition selection verification.')
    if args.baseline_manifest:
        manifest_verification = validate_manifest_selection(before, after, args.baseline_manifest, args.candidate_manifest)
    configuration_changes = validate_configuration(before, after, args.allow_ocr_change, args.allow_model_change, manifest_verification is not None)
    clusters = {}
    if args.pairs:
        for pair in json.loads(args.pairs.read_text())['pairs']:
            for identity in pair:
                clusters[str(identity)] = str(pair[0])
    if 'evaluation' in before:
        comparison = extraction(before, after)
        policies = None
    else:
        if args.candidate:
            policy_a, a = policy_rows(before, args.policy_a)
            policy_b, b = policy_rows(after, args.policy_b)
        else:
            policy_a, a = policy_rows(before, args.policy_a or 'rank')
            policy_b, b = policy_rows(before, args.policy_b or 'diverse')
        policies = [policy_a, policy_b]
        if args.selection:
            wanted = {str(query['id']) for query in json.loads(args.selection.read_text())['queries']}
            if not wanted <= {str(row['id']) for row in a} or not wanted <= {str(row['id']) for row in b}:
                raise ValueError('A compared report lacks queries from the locked selection.')
            a, b = [row for row in a if str(row['id']) in wanted], [row for row in b if str(row['id']) in wanted]
        comparison = retrieval(a, b, clusters)
    report = {'version': 1, 'baseline': str(args.baseline), 'candidate': str(args.candidate) if args.candidate else None,
              'policy_comparison': policies,
              'selection_manifest': str(args.selection) if args.selection else None,
              'selection_manifest_sha256': hashlib.sha256(args.selection.read_bytes()).hexdigest() if args.selection else None,
              'controlled_configuration_changes': configuration_changes,
              'manifest_selection_verification': manifest_verification,
              'bootstrap_note': 'Paired descriptive uncertainty; translation-pair/PDF clusters; fixed seed; no multiple-comparison correction; not a new leaderboard score.',
              'embedding_policy_before': before.get('embedding_policy'), 'embedding_policy_after': after.get('embedding_policy'),
              'comparison': comparison}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n')
    print(args.output)


if __name__ == '__main__':
    main()
