# EvoFlow: measured inference demand

Preliminary live experiment on fictional operational data. All three conditions used the same free model, provider, temperature, generation budget, interpreter, verifier, approved knowledge corpus and explicit preference snapshot. Each condition had its own SQLite state and skill library.

Two acquisition tasks plus six sealed final tasks per condition (two paired repetitions of three task variants). Candidates were frozen before final testing; final feedback did not change skills or trigger repairs.

| Condition | All tasks correct | Sealed final correct | Model calls | Input tokens | Output tokens | Reported model cost |
|---|---:|---:|---:|---:|---:|---:|
| none | 7/8 | 5/6 | 8 | 15,165 | 5,226 | $0.00 |
| frozen | 8/8 | 6/6 | 4 | 7,581 | 2,512 | $0.00 |
| evolving | 8/8 | 6/6 | 2 | 3,772 | 1,273 | $0.00 |

Evolving skills used 75% fewer model calls and 75.3% fewer reported tokens than no reuse, including acquisition. It completed all eight tasks; the no-reuse condition failed one final output-contract check.

Each covered reuse task required zero inference calls. Cold acquisition still requires model generation and independent development checks. On this workload, the first successful covered reuse starts amortizing acquisition; a changed schema requires another acquisition. This is an observed small-sample result, not a general break-even guarantee.

| Condition | Final latency mean | Sample standard deviation | Local execution total (including checks) |
|---|---:|---:|---:|
| none | 16.028s | 6.917s | 34.68ms |
| frozen | 5.881s | 9.219s | 122.15ms |
| evolving | 0.225s | 0.029s | 97.78ms |

The evolving library and its old-case regressions were tested before promotion. Public benchmark.json contains aggregate totals and library freeze hashes. Raw sealed final rows, plans, events and detailed feedback are retained locally and are not published.

Memory ablation is separate: paired executions with retrieval/reflection disabled and enabled both remained correct and used the frozen procedure. The explicit warehouse-grouping preference applied only with memory enabled. These two runs are excluded from the procedural comparison above.

No dollar savings are inferred from zero versus zero free-model cost. Tokens are inference-demand measurements, not GPU, FLOP, energy, or hardware-compute measurements. Local worker timing excludes native startup and cloud synchronization. Supabase service cost was not individually metered. Development model-selection trials are separate from this frozen benchmark and include rejected/failed generations.

Recorded failures:

- none: c00c7845-acc3-4f82-9586-b140f6840211 — Executable plan rejected: Program must end with the exact seven-column output contract
