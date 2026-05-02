<script lang="ts">
	import { page } from '$app/stores';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import {
		getBudget,
		getCurrentBudgetReport,
		createPeriod,
		updateAllocation,
		closePeriod,
		listCategories
	} from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Budget, BudgetReport, Category } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	const budgetId = $derived($page.params.id!);

	let budget = $state<Budget | null>(null);
	let report = $state<BudgetReport | null>(null);
	let categories = $state<Category[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);

	// Create period modal
	let showPeriodModal = $state(false);
	let periodStartDate = $state('');
	let periodAllocations = $state<Record<string, number>>({});
	let creatingPeriod = $state(false);
	let periodError = $state<string | null>(null);

	// Inline edit
	let editingCategory = $state<string | null>(null);
	let editAmount = $state<number>(0);
	let editRollover = $state(false);
	let savingAllocation = $state(false);

	$effect(() => {
		if (auth.user && budgetId) {
			loadBudget();
		}
	});

	async function loadBudget() {
		loading = true;
		error = null;
		try {
			const [b, cRes] = await Promise.all([getBudget(budgetId), listCategories()]);
			budget = b;
			categories = cRes.categories;
			if (b.currentPeriod) {
				report = await getCurrentBudgetReport(budgetId);
			}
			// Initialize allocation form
			const init: Record<string, number> = {};
			for (const cat of cRes.categories) {
				init[cat.id] = 0;
			}
			periodAllocations = init;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load budget';
		} finally {
			loading = false;
		}
	}

	async function handleCreatePeriod() {
		if (!budget) return;
		creatingPeriod = true;
		periodError = null;
		try {
			const allocs = Object.entries(periodAllocations)
				.filter(([, v]) => v > 0)
				.map(([categoryId, amountMinor]) => ({ categoryId, amountMinor: Math.round(amountMinor * 100) }));

			await createPeriod(budgetId, {
				startDate: periodStartDate,
				allocations: allocs
			});
			showPeriodModal = false;
			await loadBudget();
		} catch (e) {
			periodError = e instanceof Error ? e.message : 'Failed to create period';
		} finally {
			creatingPeriod = false;
		}
	}

	async function handleUpdateAllocation(categoryId: string) {
		if (!budget?.currentPeriod) return;
		savingAllocation = true;
		try {
			await updateAllocation(budgetId, budget.currentPeriod.id, categoryId, {
				amountMinor: Math.round(editAmount * 100),
				rolloverEnabled: editRollover
			});
			editingCategory = null;
			await loadBudget();
		} catch (e) {
			alert(e instanceof Error ? e.message : 'Failed to update');
		} finally {
			savingAllocation = false;
		}
	}

	async function handleClosePeriod() {
		if (!budget?.currentPeriod) return;
		if (!confirm('Close this period? Rollover balances will be carried forward.')) return;
		try {
			await closePeriod(budgetId, budget.currentPeriod.id);
			await loadBudget();
		} catch (e) {
			alert(e instanceof Error ? e.message : 'Failed to close period');
		}
	}

	function startEdit(categoryId: string, currentMinor: number, rollover: boolean) {
		editingCategory = categoryId;
		editAmount = currentMinor / 100;
		editRollover = rollover;
	}

	function categoryName(id: string): string {
		return categories.find((c) => c.id === id)?.name ?? 'Unknown';
	}

	function percentUsed(spent: number, allocated: number): number {
		if (allocated === 0) return 0;
		return Math.min(100, Math.max(0, (-spent / allocated) * 100));
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-5xl p-6">
		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else if budget}
			<header class="mb-8 flex items-center justify-between">
				<div>
					<h1 class="text-2xl font-semibold text-gray-900">{budget.name}</h1>
					<p class="text-sm text-gray-500 capitalize">
						{budget.period} · {budget.style} · {budget.currency}
					</p>
				</div>
				<div class="flex gap-3">
					{#if budget.currentPeriod}
						<button
							onclick={handleClosePeriod}
							class="rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700"
						>
							Close period
						</button>
					{:else}
						<button
							onclick={() => (showPeriodModal = true)}
							class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
						>
							Start period
						</button>
					{/if}
				</div>
			</header>

			{#if report}
				<!-- Summary cards -->
				<div class="mb-8 grid gap-4 sm:grid-cols-3">
					<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
						<p class="text-sm text-gray-500">Allocated</p>
						<p class="mt-1 text-xl font-semibold text-gray-900">
							<MoneyDisplay amount={report.totals.allocatedMinor} currency={report.totals.currency} />
						</p>
					</div>
					<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
						<p class="text-sm text-gray-500">Spent</p>
						<p class="mt-1 text-xl font-semibold text-red-600">
							<MoneyDisplay amount={report.totals.spentMinor} currency={report.totals.currency} />
						</p>
					</div>
					<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
						<p class="text-sm text-gray-500">Remaining</p>
						<p class="mt-1 text-xl font-semibold text-green-600">
							<MoneyDisplay
								amount={report.totals.remainingMinor}
								currency={report.totals.currency}
							/>
						</p>
					</div>
				</div>

				<!-- Category breakdown -->
				<div class="rounded-xl bg-white p-6 shadow-sm ring-1 ring-gray-100">
					<h2 class="mb-4 text-lg font-semibold text-gray-900">Categories</h2>
					<div class="space-y-4">
						{#each report.byCategory as item}
							<div class="flex items-center gap-4">
								<div class="flex-1">
									<div class="flex items-center justify-between">
										<span class="text-sm font-medium text-gray-900">{item.name}</span>
										{#if editingCategory === item.categoryId}
											<div class="flex items-center gap-2">
												<input
													type="number"
													step="0.01"
													bind:value={editAmount}
													class="w-24 rounded border border-gray-300 px-2 py-1 text-sm"
												/>
												<label class="flex items-center gap-1 text-xs">
													<input type="checkbox" bind:checked={editRollover} />
													Rollover
												</label>
												<button
													onclick={() => handleUpdateAllocation(item.categoryId)}
													disabled={savingAllocation}
													class="rounded bg-blue-600 px-2 py-1 text-xs text-white"
												>
													Save
												</button>
												<button
													onclick={() => (editingCategory = null)}
													class="rounded bg-gray-200 px-2 py-1 text-xs text-gray-700"
												>
													Cancel
												</button>
											</div>
										{:else}
											<button
												onclick={() =>
													startEdit(
														item.categoryId,
														item.allocatedMinor,
														budget!.currentPeriod!.allocations.find(
															(a) => a.categoryId === item.categoryId
														)?.rolloverEnabled ?? false
													)}
												class="text-sm text-blue-600 hover:underline"
											>
												<MoneyDisplay
													amount={item.allocatedMinor}
													currency={item.currency}
												/>
											</button>
										{/if}
									</div>
									<div class="mt-1 h-2 w-full overflow-hidden rounded-full bg-gray-100">
										<div
											class="h-full rounded-full {item.percentUsed > 90
												? 'bg-red-500'
												: item.percentUsed > 75
													? 'bg-amber-500'
													: 'bg-blue-500'}"
											style="width: {percentUsed(item.spentMinor, item.allocatedMinor)}%"
										></div>
									</div>
									<div class="mt-1 flex justify-between text-xs text-gray-500">
										<span>
											Spent: <MoneyDisplay
												amount={item.spentMinor}
												currency={item.currency}
											/>
										</span>
										<span>
											Rem: <MoneyDisplay
												amount={item.remainingMinor}
												currency={item.currency}
											/>
										</span>
									</div>
								</div>
							</div>
						{/each}
					</div>
				</div>
			{:else}
				<div class="rounded-xl bg-white p-8 text-center shadow-sm ring-1 ring-gray-100">
					<p class="text-gray-500">No active period. Start a period to track spending.</p>
				</div>
			{/if}
		{/if}
	</div>

	{#if showPeriodModal}
		<div
			class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
			onclick={(e) => {
				if (e.target === e.currentTarget) showPeriodModal = false;
			}}
		>
			<div class="w-full max-w-lg rounded-xl bg-white p-6 shadow-lg">
				<h2 class="mb-4 text-lg font-semibold text-gray-900">Start new period</h2>

				<div class="mb-4">
					<label class="mb-1 block text-sm font-medium text-gray-700">Start date</label>
					<input
						type="date"
						bind:value={periodStartDate}
						class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
					/>
				</div>

				<div class="max-h-80 overflow-y-auto">
					<h3 class="mb-2 text-sm font-medium text-gray-700">Allocations</h3>
					<div class="space-y-2">
						{#each categories as cat}
							<div class="flex items-center gap-3">
								<span class="w-32 text-sm text-gray-700">{cat.name}</span>
								<input
									type="number"
									step="0.01"
									bind:value={periodAllocations[cat.id]}
									class="flex-1 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
									placeholder="0.00"
								/>
							</div>
						{/each}
					</div>
				</div>

				{#if periodError}
					<p class="mt-3 text-sm text-red-600">{periodError}</p>
				{/if}

				<div class="mt-6 flex justify-end gap-3">
					<button
						onclick={() => (showPeriodModal = false)}
						class="rounded-md px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100"
					>
						Cancel
					</button>
					<button
						onclick={handleCreatePeriod}
						disabled={!periodStartDate || creatingPeriod}
						class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
					>
						{creatingPeriod ? 'Creating…' : 'Start period'}
					</button>
				</div>
			</div>
		</div>
	{/if}
{/if}
