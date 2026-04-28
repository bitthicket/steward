namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateCategoryRequest = {
    name: string
    parentId: Guid option
    rolloverEnabled: bool option
    currency: string
}

type UpdateCategoryRequest = {
    name: string option
    parentId: Guid option
    rolloverEnabled: bool option
}

type CategoryResponse = {
    id: Guid
    name: string
    parentId: Guid option
    isSystem: bool
    currency: string
    rolloverEnabled: bool
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
}

type CategoryTreeNode = {
    id: Guid
    name: string
    isSystem: bool
    currency: string
    rolloverEnabled: bool
    children: CategoryTreeNode list
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private CategoryJson =
    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let deserialize<'T> (doc: JsonDocument) =
        JsonSerializer.Deserialize<'T>(doc, jsonOptions)

// ── Domain helpers ─────────────────────────────────────────────────────────

module private CategoryHelpers =
    let categoryToResponse (cat: Category) : CategoryResponse =
        {
            id = cat.Id
            name = cat.Name
            parentId = cat.ParentCategoryId
            isSystem = cat.IsSystem
            currency = cat.CurrencyCode
            rolloverEnabled = cat.RolloverEnabled
            createdAt = cat.CreatedAt
            updatedAt = cat.UpdatedAt
        }

    let validateName (name: string) : bool =
        not (String.IsNullOrWhiteSpace(name))

    let validateCurrency (currency: string) : bool =
        not (String.IsNullOrWhiteSpace(currency)) && currency.Length = 3

    /// Build a nested tree from a flat list of categories.
    let buildTree (categories: Category list) : CategoryTreeNode list =
        let byParent =
            categories
            |> List.groupBy (fun c -> c.ParentCategoryId)
            |> Map.ofList

        let rec buildNode (cat: Category) : CategoryTreeNode =
            let children =
                byParent
                |> Map.tryFind (Some cat.Id)
                |> Option.defaultValue []
                |> List.sortBy (fun c -> c.Name)
                |> List.map buildNode

            {
                id = cat.Id
                name = cat.Name
                isSystem = cat.IsSystem
                currency = cat.CurrencyCode
                rolloverEnabled = cat.RolloverEnabled
                children = children
            }

        categories
        |> List.filter (fun c -> c.ParentCategoryId.IsNone)
        |> List.sortBy (fun c -> c.Name)
        |> List.map buildNode

// ── Endpoints ──────────────────────────────────────────────────────────────

module CategoryEndpoints =
    open CategoryHelpers

    // GET /api/categories[?tree=true]
    let listCategoriesHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! categories = repo.ListAsync()

            let q = ctx.Request.Query
            let tree =
                match q.TryGetValue("tree") with
                | true, v when v.Count > 0 -> v.ToString().ToLowerInvariant() = "true"
                | _ -> false

            if tree then
                let treeNodes = buildTree categories
                do! Response.ofJson {| categories = treeNodes |} ctx
            else
                let resp = categories |> List.map categoryToResponse
                do! Response.ofJson {| categories = resp |} ctx
        }

    // GET /api/categories/{categoryId:guid}
    let getCategoryHandler (categoryId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! catOpt = repo.GetAsync(categoryId)
            match catOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Category not found" |} ctx
            | Some cat ->
                do! Response.ofJson (categoryToResponse cat) ctx
        }

    // POST /api/categories
    let createCategoryHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = CategoryJson.readBody ctx
            let req = CategoryJson.deserialize<CreateCategoryRequest> doc

            if not (validateName req.name) then
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Name is required and cannot be empty or whitespace." |} ctx
            elif not (validateCurrency req.currency) then
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Currency must be a 3-character code." |} ctx
            else
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                match accessor.Context with
                | None ->
                    ctx.Response.StatusCode <- 401
                    do! Response.ofJson {| error = "Unauthorized" |} ctx
                | Some tc ->
                    let now = DateTimeOffset.UtcNow
                    let category: Category = {
                        Id = Guid.NewGuid()
                        TenantId = tc.TenantId
                        UserId = tc.UserId
                        Name = req.name.Trim()
                        ParentCategoryId = req.parentId
                        IsSystem = false
                        CurrencyCode = req.currency.ToUpperInvariant()
                        RolloverEnabled = req.rolloverEnabled |> Option.defaultValue false
                        DeletedAt = None
                        CreatedAt = now
                        UpdatedAt = now
                    }

                    // Validate parent exists (if provided)
                    match req.parentId with
                    | Some parentId ->
                        let! parentOpt = repo.GetAsync(parentId)
                        match parentOpt with
                        | None ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "Parent category not found" |} ctx
                        | Some _ ->
                            let! wouldCycle = repo.WouldCreateCycleAsync(category.Id, parentId)
                            if wouldCycle then
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = "Parent assignment would create a cycle" |} ctx
                            else
                                let! id = repo.CreateAsync(category)
                                ctx.Response.StatusCode <- 201
                                do! Response.ofJson (categoryToResponse category) ctx
                    | None ->
                        let! id = repo.CreateAsync(category)
                        ctx.Response.StatusCode <- 201
                        do! Response.ofJson (categoryToResponse category) ctx
        }

    // PATCH /api/categories/{categoryId:guid}
    let updateCategoryHandler (categoryId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = CategoryJson.readBody ctx
            let req = CategoryJson.deserialize<UpdateCategoryRequest> doc

            let! catOpt = repo.GetAsync(categoryId)
            match catOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Category not found" |} ctx
            | Some cat ->
                let updatedName = req.name |> Option.map (fun n -> n.Trim()) |> Option.defaultValue cat.Name
                if not (validateName updatedName) then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Name cannot be empty or whitespace." |} ctx
                else
                    let updatedParentId = req.parentId |> Option.orElse cat.ParentCategoryId
                    let updatedRollover = req.rolloverEnabled |> Option.defaultValue cat.RolloverEnabled

                    // Validate new parent
                    match updatedParentId with
                    | Some parentId ->
                        let! parentOpt = repo.GetAsync(parentId)
                        match parentOpt with
                        | None ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "Parent category not found" |} ctx
                        | Some _ ->
                            let! wouldCycle = repo.WouldCreateCycleAsync(cat.Id, parentId)
                            if wouldCycle then
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = "Parent assignment would create a cycle" |} ctx
                            else
                                let updated = {
                                    cat with
                                        Name = updatedName
                                        ParentCategoryId = updatedParentId
                                        RolloverEnabled = updatedRollover
                                        UpdatedAt = DateTimeOffset.UtcNow
                                }
                                do! repo.UpdateAsync(updated)
                                do! Response.ofJson (categoryToResponse updated) ctx
                    | None ->
                        let updated = {
                            cat with
                                Name = updatedName
                                ParentCategoryId = None
                                RolloverEnabled = updatedRollover
                                UpdatedAt = DateTimeOffset.UtcNow
                        }
                        do! repo.UpdateAsync(updated)
                        do! Response.ofJson (categoryToResponse updated) ctx
        }

    // DELETE /api/categories/{categoryId:guid}[?reassignTo={otherCategoryId}]
    let deleteCategoryHandler (categoryId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! catOpt = repo.GetAsync(categoryId)
            match catOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Category not found" |} ctx
            | Some cat ->
                if cat.IsSystem then
                    ctx.Response.StatusCode <- 403
                    do! Response.ofJson {| error = "System categories cannot be deleted" |} ctx
                else
                    let q = ctx.Request.Query
                    let reassignToOpt =
                        match q.TryGetValue("reassignTo") with
                        | true, v when v.Count > 0 ->
                            match Guid.TryParse(v.ToString()) with true, g -> Some g | _ -> None
                        | _ -> None

                    match reassignToOpt with
                    | Some reassignTo ->
                        if reassignTo = categoryId then
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "Cannot reassign to the same category" |} ctx
                        else
                            let! targetOpt = repo.GetAsync(reassignTo)
                            match targetOpt with
                            | None ->
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = "Reassign target category not found" |} ctx
                            | Some _ ->
                                do! repo.ReassignTransactionsAsync(categoryId, reassignTo)
                                do! repo.DeleteAsync(categoryId)
                                ctx.Response.StatusCode <- 204
                                do! Response.ofEmpty ctx
                    | None ->
                        let! hasTxns = repo.HasTransactionsAsync(categoryId)
                        if hasTxns then
                            ctx.Response.StatusCode <- 409
                            do! Response.ofJson {| error = "Category has active transactions. Use ?reassignTo={categoryId} to migrate them first." |} ctx
                        else
                            do! repo.DeleteAsync(categoryId)
                            ctx.Response.StatusCode <- 204
                            do! Response.ofEmpty ctx
        }
