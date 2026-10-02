# Broker protocol v4: embedding policy compatibility

Protocol v4 is required for the corrected Granite 97M input format. Its embedding
policy includes `TokenizationVersion = bos-eos-v1`; a missing value continues to
identify the historical BOS-only format and preserves historical policy keys.

This optional JSON field is safe for a current client to read from stored legacy
data, but it is not safe to send corrected embeddings to a v3 client. That client
would discard the unknown field and recompute a legacy policy key for BOS+EOS
vectors. Allowing an older client to use a newer broker by application version
alone cannot preserve this identity.

Consequently, upgrade the desktop client, MCP adapter, and broker together. Older
clients must upgrade before using the current broker; they are not permitted to
consume its embedding responses. Protocol-major validation is enforced on both
sides before any application request. The named pipe, startup lock, instance
lock, and instance metadata are also isolated by protocol major, so v3 endpoints
are not reused as v4 endpoints. An independently running older installation may
still use its own v3 broker; this change does not terminate legacy processes.

Within v4, the existing application-deployment rules remain unchanged. A newer
client replaces an older broker, conflicting builds of the same application
version are refused, and a newer same-protocol broker can serve older same-protocol
clients. No source files, stored vectors, or legacy policy keys are rewritten by
the protocol upgrade itself.

`BrokerTokenizationProtocolTests` covers the lossy v3 policy shape, endpoint
separation, both handshake directions, and current-protocol authentication without
opening a pipe or loading an embedding model.


The current app supports only the pinned corrected Granite 97M policy. Legacy
311M enum/settings values remain decode-only; they cannot select or install a
model. Embedding request DTOs contain only text/passages and have no model selector
(an unrelated `model` JSON field is ignored, without changing the fixed 97M model).
The client validates the full returned pinned model/precision/preparation/tokenizer
policy before accepting any embedding response; unsupported 311M or historical
BOS-only responses are rejected. This is separate from reading legacy policy
identities already stored in SQLite, which does not run inference or relabel vectors.
