<script lang="ts">
	import { page } from '$app/stores';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import {
		getReconciliation,
		updateReconciliationTransactions,
		completeReconciliation,
		abortReconciliation,
		listAccounts
	} from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { ReconciliationWithTransactions, Account } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	const reconId = $derived($page.params.id!);

	let recon = $state<ReconciliationWithTransactions | null>(null);
	let accounts = $state<Account[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let includedIds = $state<Set<string>>(new Set());
	let saving = $state(false);
	let completing = $state(false);
	let showForceConfirm = $state(false);

	$effect(() => {
		if (auth.user && reconId) {
			loadRecon();
		}
	});

	async function loadRecon() {
		loading = true;
		error = null;
		try {
			const [r, aRes] = await Promise.all([getReconciliation(reconId), listAccounts()]);
			recon = r;
			accounts = aRes.accounts;
			includedIds = new Set(r.includedTransactions.map((t) => t.id));
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load reconciliation';
		} finally {
			loading = false;
		}
	}

	async function toggleTransaction(txnId: string, checked: boolean) {
		if (!recon || recon.status !== 'open') return;
		saving = true;
		try {
			const included = checked ? [txnId] : [];
			const excluded = checked ? [] : [txnId];
			await updateReconciliationTransactions(reconId, { included, excluded });
			if (checked) {
				includedIds.add(txnId);
			} else {
				includedIds.delete(txnId);
			}
			includedIds = new Set(includedIds);
			// Refresh diff
			const updated = await getReconciliation(reconId);
			recon = { ...recon, diffMinor: updated.diffMinor };
		} catch (e) {
			alert(e instanceof Error ? e.message : 'Failed to update');
		} finally {
			saving = false;
		}
	}

	async function handleComplete(force = false) {
		if (!recon) return;
		completing = true;
		try {
			await completeReconciliation(reconId, force);
			goto('/reconciliation');
		} catch (e: any) {
			if (e.status === 409) {
				showForceConfirm = true;
			} else {
				alert(e instanceof Error ? e.message : 'Failed to complete');
			}
		} finally {
			completing = false;
		}
	}

	async function handleAbort() {
		if (!recon) return;
		if (!confirm('Abort this reconciliation? No transactions will be marked as reconciled.')) return;
		try {
			await abortReconciliation(reconId);
			goto('/reconciliation');
		} catch (e) {
			alert(e instanceof Error ? e.message : 'Failed to abort');
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

	const diffAmount = $derived(recon ? recon.diffMinor / 100 : 0);
</script>

{#if auth.user}
	<div class="mx-auto max-w-5xl p-6">
		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else if recon}
			<header class="mb-8">
				<div class="flex items-center justify-between">
					<div>
						<h1 class="text-2xl font-semibold text-gray-900">Reconciliation</h1>
						<p class="text-sm text-gray-500">
							{accountName(recon.accountId)} · Statement {formatDate(recon.statementDate)}
						</p>
					</div>
					<div class="flex gap-3">
						{#if recon.status === 'open'}
							<button
								onclick={handleAbort}
								class="rounded-md bg-gray-200 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-300"
							>
								Abort
							</button>
							<button
								onclick={() => handleComplete(false)}
								disabled={completing}
								class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
							>
								{completing ? 'Completing…' : 'Complete'}
							</button>
						{:else}
							<span
								class="inline-flex items-center rounded-full bg-green-50 px-3 py-1 text-sm font-medium text-green-700"
							>
								{recon.status}
							</span>
						{/if}
					</div>
				</div>

				<!-- Diff banner -->
				<div
					class="mt-4 rounded-lg p-4 {diffAmount === 0
						? 'border border-green-200 bg-green-50'
						: 'border border-amber-200 bg-amber-50'}"
				>
					<div class="flex items-center justify-between">
						<div>
							<p class="text-sm font-medium text-gray-700">
								Statement balance:
								<MoneyDisplay amount={recon.statementBalanceMinor} currency={recon.currency} />
							</p>
							<p class="text-sm text-gray-600">
								Included transactions sum:{' '}
								<MoneyDisplay
									amount={recon.statementBalanceMinor + recon.diffMinor}
									currency={recon.currency}
								/>
							</p>
						</div>
						<div class="text-right">
							<p class="text-lg font-semibold {diffAmount === 0 ? 'text-green-700' : 'text-amber-700'}">
								{#if diffAmount === 0}
									Balanced ✓
								{:else}
									Diff: <MoneyDisplay amount={recon.diffMinor} currency={recon.currency} />
								{/if}
							</p>
						</div>
					</div>
				</div>
			</header>

			{#if recon.status === 'open'}
				<p class="mb-4 text-sm text-gray-500">
					{saving ? 'Saving…' : 'Check transactions that appear on your statement.'}
				</p>
			{/if}

			<div class="divide-y divide-gray-100 rounded-xl bg-white shadow-sm ring-1 ring-gray-100">
				{#each recon.includedTransactions as txn}
					<div class="flex items-center gap-4 p-4">
						{#if recon.status === 'open'}
							<input
								type="checkbox"
								checked={includedIds.has(txn.id)}
								onchange={(e) => toggleTransaction(txn.id, e.currentTarget.checked)}
								class="h-4 w-4 rounded border-gray-300 text-blue-600"
							/>
						{:else}
							<span class="text-green-600">✓</span>
						{/if}
						<div class="flex-1">
							<p class="text-sm font-medium text-gray-900">{txn.description}</p>
							<p class="text-xs text-gray-500">
								{formatDate(txn.occurredAt)}
								{#if txn.postedAt}
									· Posted {formatDate(txn.postedAt)}
								{/if}
								{#if txn.merchant}
									· {txn.merchant}
								{/if}
							</p>
						</div>
						<span
							class="text-sm font-medium {txn.amount.amount < 0 ? 'text-red-600' : 'text-green-600'}"
						>
							<MoneyDisplay amount={txn.amount.amount} currency={txn.amount.currencyCode} />
						</span>
					</div>
				{:else}
					<div class="p-8 text-center text-gray-500">
						<p>No transactions included.</p>
					</div>
				{/each}
			</div>
		{/if}
	</div>

	{#if showForceConfirm}
		<div
			class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
			onclick={(e) => {
				if (e.target === e.currentTarget) showForceConfirm = false;
			}}
		>
			<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
				<h2 class="mb-2 text-lg font-semibold text-gray-900">Balance mismatch</h2>
				<p class="text-sm text-gray-600">
					The included transactions do not match the statement balance. Diff:{" "}
					<MoneyDisplay amount={recon?.diffMinor ?? 0} currency={recon?.currency ?? 'USD'} />
				</p>
				<p class="mt-2 text-sm text-gray-600">
					Force-complete will mark this reconciliation as done and note the discrepancy.
				</p>
				<div class="mt-6 flex justify-end gap-3">
					<button
						onclick={() => (showForceConfirm = false)}
						class="rounded-md px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100"
					>
						Cancel
					</button>
					<button
						onclick={() => {
							showForceConfirm = false;
							handleComplete(true);
						}}
						class="rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700"
					>
						Force complete
					</button>
				</div>
			</div>
		</div>
	{/if}
{/if}
