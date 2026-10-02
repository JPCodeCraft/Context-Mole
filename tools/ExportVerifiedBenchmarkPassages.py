"""Enrich a development-only exposed pool from its matching owned frozen SQLite snapshot.

The original literal excerpt/citation fields are unchanged. Full passage text is
added separately after identity, active revision, source hash, physical page and
exact UTF-16 excerpt-offset validation. Never combine generations or add neighbors.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3


def utf16_slice(text, start, length):
    if start < 0 or length < 0:
        raise ValueError('Negative excerpt bounds.')
    encoded = text.encode('utf-16-le')
    if 2 * (start + length) > len(encoded):
        raise ValueError('Excerpt exceeds the full stored passage.')
    return encoded[2 * start:2 * (start + length)].decode('utf-16-le')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--provenance', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    provenance = json.loads(args.provenance.read_text())
    snapshot_hash = hashlib.sha256(args.snapshot.read_bytes()).hexdigest()
    if provenance.get('snapshot_sha256') != snapshot_hash or not provenance.get('owned_public_benchmark_index'):
        raise ValueError('Snapshot hash/owned-index provenance is not verified.')
    if report.get('selection') != 'reranker_pilot_development_only':
        raise ValueError('Only the explicitly exported development pilot report may be enriched.')
    if report['embedding_policy']['key'] not in provenance['embedding_policies']:
        raise ValueError('Report and snapshot embedding policies differ.')
    connection = sqlite3.connect('file:' + str(args.snapshot.resolve()) + '?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row
    projects = {row['id']: row['search_generation'] for row in connection.execute('SELECT id,search_generation FROM projects')}
    expected = {row['id']: row['search_generation'] for row in provenance['project_generations']}
    if projects != expected:
        raise ValueError('Snapshot project generations differ from verified provenance.')
    source_hashes = {row['path']: row['sha256'] for row in provenance['source_documents']}
    verified = 0
    for query in report['queries']:
        for evidence in query['exposed_evidence']:
            if not evidence['literal_citation_valid']:
                raise ValueError('An invalid literal citation cannot be enriched as verified.')
            passage = connection.execute('''SELECT p.id,d.id AS document_id,p.content_id,p.display_text,p.page,
                p.revision_id,d.active_revision_id,d.path,d.sha256,d.project_id,c.parent_id
                FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id
                JOIN content_nodes c ON c.id=p.content_id WHERE p.id=?''', (evidence['passage_id'],)).fetchone()
            if passage is None or passage['parent_id'] is not None or passage['revision_id'] != passage['active_revision_id']:
                raise ValueError('Passage is missing, embedded, or from an inactive/different revision.')
            if passage['document_id'] != evidence['indexed_document_id'] or passage['content_id'] != evidence['content_id']:
                raise ValueError('Passage document/content identities differ from the source report.')
            if Path(passage['path']).stem != evidence['original_document_id'] or passage['page'] != evidence['page_number']:
                raise ValueError('Original document or physical page differs from the source report.')
            if source_hashes.get(passage['path']) != passage['sha256']:
                raise ValueError('Original PDF hash differs from snapshot provenance.')
            if utf16_slice(passage['display_text'], evidence['excerpt_start'], evidence['excerpt_length']) != evidence['excerpt']:
                raise ValueError('Exact UTF-16 excerpt differs; never silently combine generations or normalize citations.')
            evidence['full_passage_text'] = passage['display_text']
            evidence['full_passage_verification'] = {'exact_utf16_excerpt_verified': True,
                'passage_id': passage['id'], 'indexed_document_id': passage['document_id'], 'content_id': passage['content_id'],
                'active_revision_id': passage['active_revision_id'], 'source_sha256': passage['sha256'],
                'project_id': passage['project_id'], 'search_generation': projects[passage['project_id']],
                'stored_utf16_length': len(passage['display_text'].encode('utf-16-le')) // 2}
            verified += 1
    connection.close()
    report['full_passage_enrichment'] = {'snapshot_sha256': snapshot_hash,
        'source_report_sha256': hashlib.sha256(args.report.read_bytes()).hexdigest(), 'verified_records': verified,
        'query_count': len(report['queries']), 'original_excerpt_fields_unchanged': True,
        'neighbors_added': False, 'answer_targeted_windows_added': False,
        'limitations': 'Consumer-visible candidate pool remains capped; full text is stored passage text, not a full page or generated answer. Temporary original source paths are not claimed to remain materialized.'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
    print(args.output, verified)


if __name__ == '__main__':
    main()
