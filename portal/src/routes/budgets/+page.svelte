<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import { listBudgets, createBudget, listCategories } from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Budget, Category } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	let budgets = $state<Budget[]>([]);
	let categories = $state<Category[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let showCreateModal = $state(false);

	// Create form state
	let newName = $state('');
	let newPeriod = $state<'monthly' | 'biweekly' | 'weekly'>('monthly');
	let newCurrency = $state('USD');
	let newStyle = $state<'zeroBased' | 'envelope' | 'flexible' | 'traditionalLimits'>('zeroBased');
	let newIncome = $state<number | null>(null);
	let creating = $state(false);
	let createError = $state<string | null>(null);

	$effect(() => {
		if (auth.user) {
			loadBudgets();
		}
	});

	async function loadBudgets() {
		loading = true;
		error = null;
		try {
			const [bRes, cRes] = await Promise.all([listBudgets(), listCategories()]);
			budgets = bRes.budgets;
			categories = cRes.categories;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load budgets';
		} finally {
			loading = false;
		}
	}

	async function handleCreate() {
		creating = true;
		createError = null;
		try {
			await createBudget({
				name: newName,
				period: newPeriod,
				currency: newCurrency,
				style: newStyle,
				income: newIncome ?? undefined
			});
			showCreateModal = false;
			newName = '';
			newIncome = null;
			await loadBudgets();
		} catch (e) {
			createError = e instanceof Error ? e.message : 'Failed to create budget';
		} finally {
			creating = false;
		}
	}

	function allocatedTotal(budget: Budget): number {
		if (!budget.currentPeriod) return 0;
		return budget.currentPeriod.allocations.reduce((s, a) => s + a.allocatedMinor, 0);
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleDateString('en-US', {
			month: 'short',
			year: 'numeric'
		});
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-5xl p-6">
		<header class="mb-8 flex items-center justify-between">
			<div>
				<h1 class="text-2xl font-semibold text-gray-900">Budgets</h1>
				<p class="text-sm text-gray-500">Track spending against your plans</p>
			</div>
			<button
				onclick={() => (showCreateModal = true)}
				class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
			>
				+ New budget
			</button>
		</header>

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else if budgets.length === 0}
			<div class="rounded-xl bg-white p-8 text-center shadow-sm ring-1 ring-gray-100">
				<p class="text-gray-500">No budgets yet. Create your first budget to get started.</p>
			</div>
		{:else}
			<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
				{#each budgets as budget}
					<a
						href="/budgets/{budget.id}"
						class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100 transition hover:shadow-md"
					>
						<div class="flex items-start justify-between">
							<div>
								<p class="text-sm font-medium text-gray-900">{budget.name}</p>
								<p class="text-xs text-gray-500 capitalize">{budget.period} · {budget.style}</p>
							</div>
							{#if !budget.isActive}
								<span
									class="inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600"
								>
									Inactive
								</span>
							{/if}
						</div>

						{#if budget.currentPeriod}
							<div class="mt-4">
								<div class="flex items-center justify-between text-sm">
									<span class="text-gray-500">Allocated</span>
									<span class="font-medium text-gray-900">
										<MoneyDisplay amount={allocatedTotal(budget)} currency={budget.currency} />
									</span>
								</div>
								<div class="mt-2 h-2 w-full overflow-hidden rounded-full bg-gray-100">
									<div
										class="h-full rounded-full bg-blue-500"
										style="width: {Math.min(
											100,
											(budget.incomeMinor ? allocatedTotal(budget) / budget.incomeMinor * 100 : 0)
										)}%"
									></div>
								</div>
								<p class="mt-1 text-xs text-gray-400">
									Period {formatDate(budget.currentPeriod.startDate)}
								</p>
							</div>
						{:else}
							<p class="mt-4 text-xs text-amber-600">No active period</p>
						{/if}
					</a>
				{/each}
			</div>
		{/if}
	</div>

	{#if showCreateModal}
		<div
			class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
			onclick={(e) => {
				if (e.target === e.currentTarget) showCreateModal = false;
			}}
		>
			<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
				<h2 class="mb-4 text-lg font-semibold text-gray-900">Create budget</h2>

				<div class="space-y-4">
					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Name</label>
						<input
							type="text"
							bind:value={newName}
							class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							placeholder="e.g. Monthly Household"
						/>
					</div>

					<div class="grid grid-cols-2 gap-4">
						<div>
							<label class="mb-1 block text-sm font-medium text-gray-700">Period</label>
							<select
								bind:value={newPeriod}
								class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							>
								<option value="monthly">Monthly</option>
								<option value="biweekly">Bi-weekly</option>
								<option value="weekly">Weekly</option>
							</select>
						</div>
						<div>
							<label class="mb-1 block text-sm font-medium text-gray-700">Currency</label>
							<select
								bind:value={newCurrency}
								class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							>
								<option value="USD">USD</option>
								<option value="BTC">BTC</option>
							</select>
						</div>
					</div>

					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Style</label>
						<select
							bind:value={newStyle}
							class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
						>
							<option value="zeroBased">Zero-based</option>
							<option value="envelope">Envelope</option>
							<option value="flexible">Flexible</option>
							<option value="traditionalLimits">Traditional limits</option>
						</select>
					</div>

					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Income</label>
						<input
							type="number"
							step="0.01"
							bind:value={newIncome}
							class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							placeholder="Optional"
						/>
					</div>
				</div>

				{#if createError}
					<p class="mt-3 text-sm text-red-600">{createError}</p>
				{/if}

				<div class="mt-6 flex justify-end gap-3">
					<button
						onclick={() => (showCreateModal = false)}
						class="rounded-md px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100"
					>
						Cancel
					</button>
					<button
						onclick={handleCreate}
						disabled={!newName || creating}
						class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
					>
						{creating ? 'Creating…' : 'Create'}
					</button>
				</div>
			</div>
		</div>
	{/if}
{/if}
