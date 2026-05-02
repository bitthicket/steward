<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import { listAccounts, getBalance, createAccount, deleteAccount } from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Account } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	let accounts = $state<Account[]>([]);
	let balances = $state<Record<string, { posted: number; currency: string }>>({});
	let loading = $state(true);
	let error = $state<string | null>(null);
	let showCreate = $state(false);
	let creating = $state(false);
	let createError = $state<string | null>(null);
	let deleteConfirmId = $state<string | null>(null);

	// Create form
	let newName = $state('');
	let newType = $state('checking');
	let newCurrency = $state('USD');
	let newOnBudget = $state(true);
	let newInstitution = $state('');

	$effect(() => {
		if (auth.user) loadAccounts();
	});

	async function loadAccounts() {
		loading = true;
		error = null;
		try {
			const res = await listAccounts();
			accounts = res.accounts;
			const results = await Promise.all(
				accounts.map((a) =>
					getBalance(a.id)
						.then((b) => ({ id: a.id, posted: b.posted.amount, currency: b.posted.currencyCode }))
						.catch(() => ({ id: a.id, posted: 0, currency: a.currency }))
				)
			);
			balances = Object.fromEntries(results.map((b) => [b.id, b]));
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load accounts';
		} finally {
			loading = false;
		}
	}

	async function handleCreate() {
		creating = true;
		createError = null;
		try {
			await createAccount({
				name: newName,
				accountType: newType,
				currency: newCurrency.toUpperCase(),
				isOnBudget: newOnBudget,
				institutionName: newInstitution || undefined
			});
			showCreate = false;
			newName = '';
			newType = 'checking';
			newCurrency = 'USD';
			newOnBudget = true;
			newInstitution = '';
			await loadAccounts();
		} catch (e) {
			createError = e instanceof Error ? e.message : 'Failed to create account';
		} finally {
			creating = false;
		}
	}

	async function handleDelete(id: string) {
		try {
			await deleteAccount(id);
			deleteConfirmId = null;
			await loadAccounts();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to delete account';
		}
	}

	function accountTypeLabel(type: string): string {
		const labels: Record<string, string> = {
			checking: 'Checking',
			savings: 'Savings',
			creditCard: 'Credit Card',
			investment: 'Investment',
			loan: 'Loan',
			cash: 'Cash'
		};
		return labels[type] || type;
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-4xl p-6">
		<header class="mb-8 flex items-center justify-between">
			<div>
				<a href="/" class="text-sm text-blue-600 hover:underline">← Dashboard</a>
				<h1 class="mt-1 text-2xl font-semibold text-gray-900">Accounts</h1>
			</div>
			<button
				onclick={() => (showCreate = true)}
				class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
			>
				+ New account
			</button>
		</header>

		{#if error}
			<div class="mb-4 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{/if}

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if accounts.length === 0}
			<div class="rounded-xl bg-white p-8 text-center shadow-sm ring-1 ring-gray-100">
				<p class="text-gray-500">No accounts yet. Create your first account to get started.</p>
			</div>
		{:else}
			<div class="space-y-3">
				{#each accounts as account}
					<div
						class="flex items-center justify-between rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100"
					>
						<a href="/accounts/{account.id}" class="flex-1">
							<div class="flex items-center gap-3">
								<div
									class="flex h-10 w-10 items-center justify-center rounded-full bg-blue-50 text-sm font-bold text-blue-700"
								>
									{account.name.slice(0, 2).toUpperCase()}
								</div>
								<div>
									<p class="text-sm font-medium text-gray-900">{account.name}</p>
									<p class="text-xs text-gray-500">
										{accountTypeLabel(account.accountType)}
										{#if account.institutionName}
											· {account.institutionName}
										{/if}
										{#if account.isOnBudget}
											· <span class="text-green-600">On budget</span>
										{:else}
											· <span class="text-gray-400">Off budget</span>
										{/if}
									</p>
								</div>
							</div>
						</a>
						<div class="flex items-center gap-4">
							<p class="text-sm font-semibold text-gray-900">
								{#if balances[account.id]}
									<MoneyDisplay
										amount={balances[account.id].posted}
										currency={balances[account.id].currency}
									/>
								{:else}
									—
								{/if}
							</p>
							{#if deleteConfirmId === account.id}
								<div class="flex items-center gap-2">
									<button
										onclick={() => handleDelete(account.id)}
										class="rounded bg-red-600 px-2 py-1 text-xs font-medium text-white hover:bg-red-700"
									>
										Confirm
									</button>
									<button
										onclick={() => (deleteConfirmId = null)}
										class="rounded bg-gray-100 px-2 py-1 text-xs text-gray-700 hover:bg-gray-200"
									>
										Cancel
									</button>
								</div>
							{:else}
								<button
									onclick={() => (deleteConfirmId = account.id)}
									class="text-xs text-red-600 hover:text-red-700"
								>
									Delete
								</button>
							{/if}
						</div>
					</div>
				{/each}
			</div>
		{/if}
	</div>
{/if}

{#if showCreate}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
		<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
			<h2 class="mb-4 text-lg font-semibold text-gray-900">Create account</h2>
			{#if createError}
				<div class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
					{createError}
				</div>
			{/if}
			<div class="space-y-4">
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Name</label>
					<input
						type="text"
						bind:value={newName}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
						placeholder="e.g. Main Checking"
					/>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Type</label>
					<select
						bind:value={newType}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
					>
						<option value="checking">Checking</option>
						<option value="savings">Savings</option>
						<option value="creditCard">Credit Card</option>
						<option value="investment">Investment</option>
						<option value="loan">Loan</option>
						<option value="cash">Cash</option>
					</select>
				</div>
				<div class="grid grid-cols-2 gap-3">
					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Currency</label>
						<input
							type="text"
							bind:value={newCurrency}
							maxlength={3}
							class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm uppercase focus:border-blue-500 focus:outline-none"
						/>
					</div>
					<div class="flex items-end">
						<label class="flex items-center gap-2 text-sm text-gray-700">
							<input type="checkbox" bind:checked={newOnBudget} class="rounded" />
							On budget
						</label>
					</div>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Institution (optional)</label>
					<input
						type="text"
						bind:value={newInstitution}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
						placeholder="e.g. Chase"
					/>
				</div>
			</div>
			<div class="mt-6 flex justify-end gap-3">
				<button
					onclick={() => (showCreate = false)}
					class="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200"
				>
					Cancel
				</button>
				<button
					onclick={handleCreate}
					disabled={creating || !newName.trim()}
					class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
				>
					{creating ? 'Creating…' : 'Create'}
				</button>
			</div>
		</div>
	</div>
{/if}
