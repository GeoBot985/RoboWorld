# Characterized controller profile builder

This utility converts measured controller and device observations into a versioned deterministic-profile candidate. It preserves source-trace hashes, checks the training/held-out separation, and replays held-out traces through the same bounded evaluator used by the Domain backend.

The generated candidate is not accepted as a replacement for Renode. Current broad controller, sensor, and performance qualification remains incomplete; the Exact Renode path stays the reference backend. See [ADR-020](../../docs/architecture/adr-020-characterized-controller-backends.md) and [validation scope](../../docs/validation.md).

Build with the pinned .NET SDK and run the builder without untrusted input. The command-line arguments require a complete robot graph, versioned component catalogue, firmware identity, and measured training/held-out data; those campaign datasets are not included in this public snapshot.
