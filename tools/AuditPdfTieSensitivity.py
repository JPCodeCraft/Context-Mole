"""Post-hoc exact-tie risks in exposed previews and an optional matching vector snapshot.

This diagnoses ambiguity; it does not replay alternative IDs, reconstruct suppressed
previews, or assert a tie caused a measured gain. Initial reports may lack raw
similarities. A matching snapshot is required for byte-identical vector groups.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import sqlite3


def vector_groups(snapshot):
    connection = sqlite3.connect('file:' + str(snapshot.resolve()) + '?mode=ro', uri=True)
    groups = defaultdict(list)
    for identity, vector, path, page in connection.execute('SELECT e.passage_id,e.vector,d.path,p.page FROM embeddings e JOIN passages p ON p.rowid=e.passage_rowid JOIN documents d ON d.active_revision_id=p.revision_id'):
        groups[hashlib.sha256(vector).hexdigest()].append((identity, Path(path).stem, page))
    connection.close()
    return {identity: rows for rows in groups.values() if len(rows) > 1 for identity, _, _ in rows}


def audit(row, groups):
    evidence = row['exposed_evidence']
    scores = Counter(e['fused_score'] for e in evidence)
    excerpts = defaultdict(set)
    for e in evidence:
        text = ' '.join(e['excerpt'].split())
        if len(text) >= 40:
            excerpts[text].add((e['original_document_id'], e['page_number']))
    boundaries = []
    by_document = defaultdict(list)
    for e in evidence:
        by_document[e['original_document_id']].append(e)
    for document, records in by_document.items():
        if len(records) < 10:
            continue
        # This is a candidate for GUID-dependent raw-tie/cap ambiguity, not a replay.
        # Evidence records preserve production group preview order; root-only passage scope has one group/document.
        last = records[-1]
        peers = groups.get(last['passage_id'], [])
        visible = {e['passage_id'] for e in records}
        hidden_same_document = [peer for peer in peers if peer[1] == document and peer[0] not in visible]
        if hidden_same_document:
            boundaries.append({'document_id': document, 'last_exposed_passage_id': last['passage_id'],
                               'byte_identical_unexposed_snapshot_passages': len(hidden_same_document),
                               'pages': sorted({peer[2] for peer in hidden_same_document if peer[2] is not None})})
    return {'id': row['id'], 'language': row['language'], 'preview_policy': row.get('preview_policy', 'rank'),
            'exposed_fused_score_tie_groups': sum(count > 1 for count in scores.values()),
            'duplicate_literal_excerpts_across_pages': sum(len(pages) > 1 for pages in excerpts.values()),
            'exposed_passages_in_byte_identical_vector_groups': sum(e['passage_id'] in groups for e in evidence),
            'potential_tied_preview_cap_boundaries': boundaries}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--snapshot', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    groups = vector_groups(args.snapshot) if args.snapshot else {}
    if args.snapshot:
        ids = {e['passage_id'] for row in report['queries'] for e in row['exposed_evidence']}
        # A different generation must not be quietly enriched with unrelated vector identities.
        connection = sqlite3.connect('file:' + str(args.snapshot.resolve()) + '?mode=ro', uri=True)
        available = {row[0] for row in connection.execute('SELECT id FROM passages')}
        connection.close()
        if not ids <= available:
            raise ValueError('Report/snapshot passage identities differ; cannot diagnose another generation.')
    rows = [audit(row, groups) for row in report['queries']]
    summary = {policy: {'query_count': len(subset),
        'queries_with_exposed_fused_score_ties': sum(row['exposed_fused_score_tie_groups'] > 0 for row in subset),
        'queries_with_duplicate_literal_excerpts_across_pages': sum(row['duplicate_literal_excerpts_across_pages'] > 0 for row in subset),
        'queries_with_byte_identical_vector_peers': sum(row['exposed_passages_in_byte_identical_vector_groups'] > 0 for row in subset),
        'queries_with_potential_tied_cap_boundaries': sum(bool(row['potential_tied_preview_cap_boundaries']) for row in subset)}
        for policy in sorted({row['preview_policy'] for row in rows})
        if (subset := [row for row in rows if row['preview_policy'] == policy])}
    args.output.write_text(json.dumps({'version': 1, 'post_hoc_diagnostic': True, 'changes_existing_metrics': False,
        'report': str(args.report), 'snapshot': str(args.snapshot) if args.snapshot else None,
        'limitations': ['No alternative-GUID replay or suppressed-candidate reconstruction.',
                        'Byte-identical vectors guarantee a raw cosine tie but do not prove it drives the final cutoff.',
                        'Identical clipped excerpts alone do not guarantee identical full model inputs or vectors.',
                        'Random generation-specific passage IDs can break raw vector ties before exposed fused ranks.',
                        'Small before/after gains should be interpreted alongside paired uncertainty and literal evidence changes.'],
        'summary': summary, 'queries': rows}, indent=2) + '\n')
    print(args.output)


if __name__ == '__main__':
    main()
