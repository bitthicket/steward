<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import { listReconciliations, createReconciliation, listAccounts } from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Reconciliation, Account } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	let reconciliations = $state<Reconciliation[]>([]);
	let accounts = $state<Account[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);

	// Start new reconciliation modal
	let showStartModal = $state(false);
	let selectedAccountId = $state('');
	let statementDate = $state('');
	let statementBalance = $state<number | null>(null);
	let currency = $state('USD');
	let starting = $state(false);
	let startError = $state<string | null>(null);

	$effect(() => {
		if (auth.user) {
			loadData();
		}
	});

	async function loadData() {
		loading = true;
		error = null;
		try {
			const [rRes, aRes] = await Promise.all([listReconciliations(), listAccounts()]);
			reconciliations = rRes.reconciliations;
			accounts = aRes.accounts;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load data';
		} finally {
			loading = false;
		}
	}

	async function handleStart() {
		if (!selectedAccountId || !statementDate || statementBalance === null) return;
		starting = true;
		startError = null;
		try {
			const res = await createReconciliation({
				accountId: selectedAccountId,
				statementDate,
				statementBalanceMinor: Math.round(statementBalance * 100),
				currency
			});
			goto(`/reconciliation/${res.reconciliation.id}`);
		} catch (e) {
			startError = e instanceof Error ? e.message : 'Failed to start reconciliation';
		} finally {
			starting = false;
		}
	}

	function statusBadge(status: string): string {
		switch (status) {
			case 'open':
				return 'bg-blue-50 text-blue-700';
			case 'completed':
				return 'bg-green-50 text-green-700';
			case 'aborted':
				return 'bg-gray-50 text-gray-600';
			default:
				return 'bg-gray-50 text-gray-600';
		}
	}

	function accountName(id: string): string {
		return accounts.find((a) => a.id === id)?.name ?? 'Unknown';
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleDateString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric'
		});
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-5xl p-6">
		<header class="mb-8 flex items-center justify-between">
			<div>
				<h1 class="text-2xl font-semibold text-gray-900">Reconciliation</h1>
				<p class="text-sm text-gray-500">Match transactions to bank statements</p>
			</div>
			<button
				onclick={() => (showStartModal = true)}
				class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
			>
				+ New reconciliation
			</button>
		</header>

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else}
			{#if reconciliations.length === 0}
				<div class="rounded-xl bg-white p-8 text-center shadow-sm ring-1 ring-gray-100">
					<p class="text-gray-500">No reconciliations yet. Start one to match your statement.</p>
				</div>
			{:else}
				<div class="divide-y divide-gray-100 rounded-xl bg-white shadow-sm ring-1 ring-gray-100">
					{#each reconciliations as recon}
						<a
							href="/reconciliation/{recon.id}"
							class="flex items-center justify-between p-4 transition hover:bg-gray-50"
						>
							<div>
								<p class="text-sm font-medium text-gray-900">{accountName(recon.accountId)}</p>
								<p class="text-xs text-gray-500">
									Statement {formatDate(recon.statementDate)} ·
									<MoneyDisplay
										amount={recon.statementBalanceMinor}
										currency={recon.currency}
									/>
								</p>
							</div>
							<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {statusBadge(recon.status)}">
								{recon.status}
							</span>
						</a>
					{/each}
				</div>
			{/if}
		{/if}
	</div>

	{#if showStartModal}
		<div
			class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
			onclick={(e) => {
				if (e.target === e.currentTarget) showStartModal = false;
			}}
		>
			<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
				<h2 class="mb-4 text-lg font-semibold text-gray-900">Start reconciliation</h2>

				<div class="space-y-4">
					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Account</label>
						<select
							bind:value={selectedAccountId}
							class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
						>
							<option value="">Select account</option>
							{#each accounts as account}
								<option value={account.id}>{account.name} ({account.currency})</option>
							{/each}
						</select>
					</div>

					<div>
						<label class="mb-1 block text-sm font-medium text-gray-700">Statement date</label>
						<input
							type="date"
							bind:value={statementDate}
							class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
						/>
					</div>

					<div class="grid grid-cols-2 gap-4">
						<div>
							<label class="mb-1 block text-sm font-medium text-gray-700">Statement balance</label>
							<input
								type="number"
								step="0.01"
								bind:value={statementBalance}
								class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							/>
						</div>
						<div>
							<label class="mb-1 block text-sm font-medium text-gray-700">Currency</label>
							<select
								bind:value={currency}
								class="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
							>
								<option value="USD">USD</option>
								<option value="BTC">BTC</option>
							</select>
						</div>
					</div>
				</div>

				{#if startError}
					<p class="mt-3 text-sm text-red-600">{startError}</p>
				{/if}

				<div class="mt-6 flex justify-end gap-3">
					<button
						onclick={() => (showStartModal = false)}
						class="rounded-md px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100"
					>
						Cancel
					</button>
					<button
						onclick={handleStart}
						disabled={!selectedAccountId || !statementDate || statementBalance === null || starting}
						class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
					>
						{starting ? 'Starting…' : 'Start'}
					</button>
				</div>
			</div>
		</div>
	{/if}
{/if}
