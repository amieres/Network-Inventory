module Health.Correlate

open System
open Health

// ── Root-cause correlation ───────────────────────────────────────────────────
// Turns a flat list of faults into one incident per root cause. If an AC500 is
// down, the garage opener and four transfer-switch circuits hanging off it are
// consequences, not separate problems - they get Suppressed and the AC500 is
// named as the cause.
//
// This is the whole point of the dependency graph: on a bad day the difference
// between "6 things are broken" and "AC500 #2 is down, affecting 5 things".

type Verdict =
    /// Faulted with no faulted ancestor - this is the thing to go fix.
    | RootCause  of affected : string list
    /// Faulted, but an ancestor is also faulted and explains it.
    | Suppressed of because  : string * kind : EdgeKind
    /// Healthy.
    | Healthy

type NodeVerdict = {
    key     : string
    label   : string
    faulted : bool
    verdict : Verdict
}

/// `isFaulted key` reports whether a topology node is currently in a fault state.
/// Nodes with no health signal at all (circuits, SSIDs) are inferred: a circuit
/// is considered faulted only when its parent is, since nothing measures it.
let analyseWith (nodes: Node list) (edges: Edge list) (isFaulted: string -> bool) : NodeVerdict list =
    let parentsOf k = edges |> List.filter (fun e -> e.child = k)
    let childrenOf k = edges |> List.filter (fun e -> e.parent = k)
    let byKey = nodes |> List.map (fun n -> n.key, n) |> Map.ofList
    let faulted = nodes |> List.map (fun n -> n.key, isFaulted n.key) |> Map.ofList
    let isF k = faulted |> Map.tryFind k |> Option.defaultValue false

    let rec descendants seen key =
        childrenOf key
        |> List.collect (fun e ->
            if Set.contains e.child seen then []
            else e.child :: descendants (Set.add e.child seen) e.child)
        |> List.distinct

    nodes
    |> List.map (fun n ->
        if not (isF n.key) then
            { key = n.key; label = n.label; faulted = false; verdict = Healthy }
        else
            match parentsOf n.key |> List.tryFind (fun e -> isF e.parent) with
            | Some e ->
                let parentLabel =
                    byKey |> Map.tryFind e.parent |> Option.map (fun p -> p.label) |> Option.defaultValue e.parent
                { key = n.key; label = n.label; faulted = true
                  verdict = Suppressed (parentLabel, e.kind) }
            | None ->
                let affected =
                    descendants (Set.singleton n.key) n.key
                    |> List.filter isF
                    |> List.choose (fun k -> byKey |> Map.tryFind k |> Option.map (fun p -> p.label))
                { key = n.key; label = n.label; faulted = true; verdict = RootCause affected })

/// Convenience wrapper over the seed topology plus its derived edges.
let analyse (isFaulted: string -> bool) : NodeVerdict list =
    analyseWith Topology.nodes (Topology.edges @ Topology.derivedEdges Topology.nodes) isFaulted

/// Just the root causes, most impactful first - what a dashboard banner shows.
let rootCauses (verdicts: NodeVerdict list) =
    verdicts
    |> List.choose (fun v -> match v.verdict with RootCause a -> Some (v, a) | _ -> None)
    |> List.sortByDescending (fun (_, affected) -> List.length affected)
