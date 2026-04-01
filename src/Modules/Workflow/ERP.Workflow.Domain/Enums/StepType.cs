namespace ERP.Workflow.Domain.Enums;

/// <summary>
/// STEP TYPE — Determines how a process step behaves in the production flow.
///
/// Maps directly to your workflow chart:
/// - Single:   Sequential step — one department, then the next
/// - Parallel: Split to multiple departments simultaneously (e.g., Dept A + Dept B)
/// - Merge:    Join parallel paths back into a single flow
///
/// EXAMPLE FLOW:
/// ┌──────────┐     ┌──────────┐     ┌───────────┐
/// │ Cutting   │ ──→ │ Bending  │ ──→ │ PARALLEL  │
/// │ (Single)  │     │ (Single) │     │ SPLIT     │
/// └──────────┘     └──────────┘     └─────┬─────┘
///                                    ┌────┴────┐
///                                    ▼         ▼
///                              ┌──────────┐ ┌──────────┐
///                              │ Painting │ │ Coating  │
///                              │ (Parallel)│ │ (Parallel)│
///                              └────┬─────┘ └─────┬────┘
///                                   └──────┬──────┘
///                                          ▼
///                                    ┌──────────┐
///                                    │ Assembly │
///                                    │ (Merge)  │
///                                    └──────────┘
/// </summary>
public enum StepType
{
    /// <summary>Sequential step — one department processes, then moves to next</summary>
    Single = 0,

    /// <summary>Parallel split — job cards are created for multiple departments simultaneously</summary>
    Parallel = 1,

    /// <summary>Merge point — waits for all parallel paths to complete before proceeding</summary>
    Merge = 2
}
