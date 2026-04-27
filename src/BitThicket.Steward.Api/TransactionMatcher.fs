namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open BitThicket.Steward.Api.Domain

// ─────────────────────────────────────────────────────────────────────────────
// Feed transaction candidate (shape produced by provider adapters)
// ─────────────────────────────────────────────────────────────────────────────

type FeedTransactionCandidate = {
    ExternalId: string
    AccountId: Guid
    OccurredAt: DateTimeOffset
    PostedAt: DateTimeOffset option
    Amount: Money
    Description: string
    Merchant: string option
}

// ─────────────────────────────────────────────────────────────────────────────
// Match result
// ─────────────────────────────────────────────────────────────────────────────

type MatchResult =
    | AutoMatched of manualTxnId: Guid * confidence: decimal
    | NeedsReview of manualTxnId: Guid * confidence: decimal
    | NoMatch

// ─────────────────────────────────────────────────────────────────────────────
// ITransactionMatcher
// ─────────────────────────────────────────────────────────────────────────────

type ITransactionMatcher =
    abstract MatchAsync : tenantId: Guid -> accountId: Guid -> candidate: FeedTransactionCandidate -> Task<MatchResult>

// ─────────────────────────────────────────────────────────────────────────────
// Jaro–Winkler similarity (pure, no deps)
// ─────────────────────────────────────────────────────────────────────────────

module StringSimilarity =

    let private jaro (s1: string) (s2: string) : float =
        if s1 = s2 then 1.0
        elif String.IsNullOrEmpty(s1) || String.IsNullOrEmpty(s2) then 0.0
        else
            let len1 = s1.Length
            let len2 = s2.Length
            let matchDistance = max len1 len2 / 2 - 1

            let s1Matches = Array.create len1 false
            let s2Matches = Array.create len2 false

            let mutable matches = 0
            let mutable transpositions = 0

            for i in 0 .. len1 - 1 do
                let start = max 0 (i - matchDistance)
                let finish = min (i + matchDistance + 1) len2
                for j in start .. finish - 1 do
                    if not s2Matches.[j] && s1.[i] = s2.[j] then
                        s1Matches.[i] <- true
                        s2Matches.[j] <- true
                        matches <- matches + 1

            if matches = 0 then 0.0
            else
                let mutable k = 0
                for i in 0 .. len1 - 1 do
                    if s1Matches.[i] then
                        while not s2Matches.[k] do
                            k <- k + 1
                        if s1.[i] <> s2.[k] then
                            transpositions <- transpositions + 1
                        k <- k + 1

                let m = float matches
                (m / float len1 + m / float len2 + (m - float transpositions / 2.0) / m) / 3.0

    let jaroWinkler (s1: string) (s2: string) : float =
        let s1 = s1.Trim().ToLowerInvariant()
        let s2 = s2.Trim().ToLowerInvariant()
        let j = jaro s1 s2
        if j < 0.7 then j
        else
            let prefixLen =
                let mutable i = 0
                while i < min 4 (min s1.Length s2.Length) && s1.[i] = s2.[i] do
                    i <- i + 1
                i
            j + 0.1 * float prefixLen * (1.0 - j)

// ─────────────────────────────────────────────────────────────────────────────
// Scoring
// ─────────────────────────────────────────────────────────────────────────────

module TransactionMatcher =

    let private autoThreshold () : decimal =
        match Environment.GetEnvironmentVariable("STEWARD_MATCH_AUTO_THRESHOLD") with
        | null | "" -> 0.9m
        | v -> match Decimal.TryParse(v) with true, d -> d | _ -> 0.9m

    let private reviewThreshold () : decimal =
        match Environment.GetEnvironmentVariable("STEWARD_MATCH_REVIEW_THRESHOLD") with
        | null | "" -> 0.6m
        | v -> match Decimal.TryParse(v) with true, d -> d | _ -> 0.6m

    /// Amount score: 0.4 if within ±$0.01 (or 1 satoshi for BTC), else 0.
    let amountScore (candidate: FeedTransactionCandidate) (manual: Transaction) : float =
        if candidate.Amount.CurrencyCode <> manual.Amount.CurrencyCode then 0.0
        else
            let places =
                match candidate.Amount.CurrencyCode.ToUpperInvariant() with
                | "BTC" -> 8
                | _ -> 2
            let factor = pown 10m places
            let cMinor = int64 (Decimal.Round(candidate.Amount.Amount * factor))
            let mMinor = int64 (Decimal.Round(manual.Amount.Amount * factor))
            if abs (cMinor - mMinor) <= 1L then 0.4 else 0.0

    /// Date score: 0.3 within ±2 days, decaying linearly to 0 at ±7 days.
    let dateScore (candidate: FeedTransactionCandidate) (manual: Transaction) : float =
        let deltaDays = abs (candidate.OccurredAt - manual.OccurredAt).TotalDays
        if deltaDays <= 2.0 then 0.3
        elif deltaDays >= 7.0 then 0.0
        else 0.3 * (7.0 - deltaDays) / 5.0

    /// Description similarity score: Jaro–Winkler weighted to 0.3.
    let descriptionScore (candidate: FeedTransactionCandidate) (manual: Transaction) : float =
        let cDesc = candidate.Description
        let mDesc = manual.Description
        let sim = StringSimilarity.jaroWinkler cDesc mDesc
        0.3 * sim

    /// Full confidence score in [0.0, 1.0].
    let score (candidate: FeedTransactionCandidate) (manual: Transaction) : decimal =
        // Required filter: same account, same currency, same sign.
        if candidate.AccountId <> manual.AccountId then 0.0m
        elif candidate.Amount.CurrencyCode <> manual.Amount.CurrencyCode then 0.0m
        elif (candidate.Amount.Amount > 0m) <> (manual.Amount.Amount > 0m) then 0.0m
        else
            let a = amountScore candidate manual
            let d = dateScore candidate manual
            let s = descriptionScore candidate manual
            decimal (a + d + s)

    /// Evaluate the best candidate from the list.
    let evaluate (candidate: FeedTransactionCandidate) (manuals: Transaction list) : MatchResult =
        let scored =
            manuals
            |> List.map (fun m -> m, score candidate m)
            |> List.filter (fun (_, sc) -> sc > 0.0m)
            |> List.sortByDescending snd

        match scored with
        | [] -> NoMatch
        | (best, conf) :: _ ->
            if conf >= autoThreshold () then AutoMatched(best.Id, conf)
            elif conf >= reviewThreshold () then NeedsReview(best.Id, conf)
            else NoMatch

    // ── Implementation ───────────────────────────────────────────────────────

    let create (repo: ITransactionRepository) : ITransactionMatcher =
        { new ITransactionMatcher with
            member _.MatchAsync tenantId accountId candidate =
                task {
                    let ctx = { TenantId = tenantId; UserId = Guid.Empty }
                    let! candidates = repo.ListMatchCandidatesAsync(accountId)
                    return evaluate candidate candidates
                }
        }
