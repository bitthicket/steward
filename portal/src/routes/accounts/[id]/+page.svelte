<script lang="ts">
	import { page } from '$app/stores';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import {
		getAccount,
		getBalance,
		listTransactions,
		updateAccount,
		createTransaction,
		updateTransaction,
		deleteTransaction
	} from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Account, Transaction } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	const accountId = $derived($page.params.id);

	let account = $state<Account | null>(null);
	let balance = $state<{ posted: number; available: number; pending: number; currency: string } | null>(null);
	let transactions = $state<Transaction[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let activeTab = $state<'transactions' | 'settings'>('transactions');

	// Filters
	let filterStatus = $state('');
	let filterFrom = $state('');
	let filterTo = $state('');

	// Edit account
	let editingName = $state(false);
	let editNameValue = $state('');
	let editOnBudget = $state(false);
	let savingAccount = $state(false);

	// Add transaction modal
	let showAddTxn = $state(false);
	let txnDate = $state('');
	let txnDescription = $state('');
	let txnMerchant = $state('');
	let txnAmountMinor = $state('');
	let txnCurrency = $state('USD');
	let addingTxn = $state(false);
	let txnError = $state<string | null>(null);

	// Inline edit
	let editTxnId = $state<string | null>(null);
	let editTxnDescription = $state('');
	let editTxnMerchant = $state('');
	let editTxnCategory = $state('');

	$effect(() => {
		if (auth.user && accountId) loadAccount();
	});

	async function loadAccount() {
		loading = true;
		error = null;
		try {
			const [acct, bal, txnRes] = await Promise.all([
				getAccount(accountId),
				getBalance(accountId),
				listTransactions({ accountId, limit: 50 })
			]);
			account = acct;
			balance = {
				posted: bal.posted.amount,
				available: bal.available.amount,
				pending: bal.pending.amount,
				currency: bal.posted.currencyCode
			};
			transactions = txnRes.transactions;
			editNameValue = acct.name;
			editOnBudget = acct.isOnBudget;
			 txnCurrency = acct.currency;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load account';
		} finally {
			loading = false;
		}
	}

	async function applyFilters() {
		try {
			const res = await listTransactions({
				accountId,
				status: filterStatus || undefined,
				from: filterFrom || undefined,
				to: filterTo || undefined,
				limit: 50
			});
			transactions = res.transactions;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to filter transactions';
		}
	}

	async function saveAccountEdit() {
		if (!account) return;
		savingAccount = true;
		try {
			await updateAccount(account.id, {
				name: editNameValue,
				isOnBudget: editOnBudget
			});
			account = { ...account, name: editNameValue, isOnBudget: editOnBudget };
			editingName = false;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to update account';
		} finally {
			savingAccount = false;
		}
	}

	async function handleAddTransaction() {
		addingTxn = true;
		txnError = null;
		try {
			const amount = parseInt(txnAmountMinor, 10);
			if (isNaN(amount)) throw new Error('Invalid amount');
			await createTransaction({
				accountId,
				occurredAt: new Date(txnDate).toISOString(),
				amountMinor: amount,
				currency: txnCurrency,
				description: txnDescription,
				merchant: txnMerchant || undefined
			});
			showAddTxn = false;
			txnDate = '';
			txnDescription = '';
			txnMerchant = '';
			txnAmountMinor = '';
			await loadAccount();
		} catch (e) {
			txnError = e instanceof Error ? e.message : 'Failed to add transaction';
		} finally {
			addingTxn = false;
		}
	}

	async function startEditTxn(txn: Transaction) {
		editTxnId = txn.id;
		editTxnDescription = txn.description;
		editTxnMerchant = txn.merchant || '';
		editTxnCategory = txn.categoryId || '';
	}

	async function saveEditTxn() {
		if (!editTxnId) return;
		try {
			await updateTransaction(editTxnId, {
				description: editTxnDescription,
				merchant: editTxnMerchant || undefined
			});
			editTxnId = null;
			await loadAccount();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to update transaction';
		}
	}

	async function handleDeleteTxn(id: string) {
		if (!confirm('Delete this transaction?')) return;
		try {
			await deleteTransaction(id);
			await loadAccount();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to delete transaction';
		}
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleDateString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric'
		});
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

	function statusBadge(status: string) {
		const classes: Record<string, string> = {
			Pending: 'bg-yellow-50 text-yellow-700',
			NeedsReview: 'bg-amber-50 text-amber-700',
			Cleared: 'bg-green-50 text-green-700',
			Reconciled: 'bg-blue-50 text-blue-700'
		};
		return classes[status] || 'bg-gray-50 text-gray-700';
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-4xl p-6">
		<header class="mb-6">
			<a href="/accounts" class="text-sm text-blue-600 hover:underline">← Accounts</a>
		</header>

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else if account}
			<div class="mb-6 rounded-xl bg-white p-6 shadow-sm ring-1 ring-gray-100">
				<div class="flex items-start justify-between">
					<div>
						{#if editingName}
							<div class="flex items-center gap-2">
								<input
									bind:value={editNameValue}
									class="rounded-md border border-gray-300 px-2 py-1 text-lg font-semibold"
								/>
								<button
									onclick={saveAccountEdit}
									disabled={savingAccount}
									class="text-xs text-blue-600 hover:text-blue-700"
								>
									Save
								</button>
								<button
									onclick={() => {
										editingName = false;
										editNameValue = account?.name ?? '';
										editOnBudget = account?.isOnBudget ?? false;
									}}
									class="text-xs text-gray-500 hover:text-gray-700"
								>
									Cancel
								</button>
							</div>
						{:else}
							<div class="flex items-center gap-2">
								<h1 class="text-2xl font-semibold text-gray-900">{account.name}</h1>
								<button
									onclick={() => {
										editingName = true;
										editNameValue = account?.name ?? '';
										editOnBudget = account?.isOnBudget ?? false;
									}}
									class="text-xs text-gray-400 hover:text-gray-600"
								>
									Edit
								</button>
							</div>
						{/if}
						<p class="text-sm text-gray-500">
							{accountTypeLabel(account.accountType)}
							{#if account.institutionName}
								· {account.institutionName}
							{/if}
						</p>
					</div>
					<div class="text-right">
						<p class="text-2xl font-bold text-gray-900">
							{#if balance}
								<MoneyDisplay amount={balance.posted} currency={balance.currency} />
							{:else}
								—
							{/if}
						</p>
						<p class="text-xs text-gray-500">
							Available:
							{#if balance}
								<MoneyDisplay amount={balance.available} currency={balance.currency} />
							{:else}
								—
							{/if}
						</p>
					</div>
				</div>
			</div>

			<!-- Tabs -->
			<div class="mb-4 border-b border-gray-200">
				<nav class="flex gap-6">
					<button
						onclick={() => (activeTab = 'transactions')}
						class="border-b-2 px-1 pb-3 text-sm font-medium {activeTab === 'transactions'
							? 'border-blue-600 text-blue-600'
							: 'border-transparent text-gray-500 hover:text-gray-700'}"
					>
						Transactions
					</button>
					<button
						onclick={() => (activeTab = 'settings')}
						class="border-b-2 px-1 pb-3 text-sm font-medium {activeTab === 'settings'
							? 'border-blue-600 text-blue-600'
							: 'border-transparent text-gray-500 hover:text-gray-700'}"
					>
						Settings
					</button>
				</nav>
			</div>

			{#if activeTab === 'transactions'}
				<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
					<div class="mb-4 flex flex-wrap items-end gap-3">
						<div>
							<label class="mb-1 block text-xs font-medium text-gray-500">From</label>
							<input
								type="date"
								bind:value={filterFrom}
								class="rounded-md border border-gray-300 px-2 py-1.5 text-sm"
							/>
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-gray-500">To</label>
							<input
								type="date"
								bind:value={filterTo}
								class="rounded-md border border-gray-300 px-2 py-1.5 text-sm"
							/>
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-gray-500">Status</label>
							<select
								bind:value={filterStatus}
								class="rounded-md border border-gray-300 px-2 py-1.5 text-sm"
							>
								<option value="">All</option>
								<option value="Pending">Pending</option>
								<option value="NeedsReview">Needs Review</option>
								<option value="Cleared">Cleared</option>
								<option value="Reconciled">Reconciled</option>
							</select>
						</div>
						<button
							onclick={applyFilters}
							class="rounded-md bg-gray-100 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-200"
						>
							Filter
						</button>
						<button
							onclick={() => (showAddTxn = true)}
							class="ml-auto rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
						>
							+ Add transaction
						</button>
					</div>

					{#if transactions.length === 0}
						<p class="py-8 text-center text-sm text-gray-500">No transactions found.</p>
					{:else}
						<div class="divide-y divide-gray-100">
							{#each transactions as txn}
								<div class="group flex items-center justify-between py-3">
									<div class="flex-1">
										{#if editTxnId === txn.id}
											<div class="flex flex-col gap-2 sm:flex-row">
												<input
													bind:value={editTxnDescription}
													class="rounded-md border border-gray-300 px-2 py-1 text-sm"
													placeholder="Description"
												/>
												<input
													bind:value={editTxnMerchant}
													class="rounded-md border border-gray-300 px-2 py-1 text-sm"
													placeholder="Merchant"
												/>
												<div class="flex gap-2">
													<button
														onclick={saveEditTxn}
														class="text-xs text-blue-600 hover:text-blue-700"
													>
														Save
													</button>
													<button
														onclick={() => (editTxnId = null)}
														class="text-xs text-gray-500 hover:text-gray-700"
													>
														Cancel
													</button>
												</div>
											</div>
										{:else}
											<p class="text-sm font-medium text-gray-900">{txn.description}</p>
											<p class="text-xs text-gray-500">
												{formatDate(txn.occurredAt)}
												{#if txn.merchant}
													· {txn.merchant}
												{/if}
												<span class="ml-2 inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium {statusBadge(txn.status)}">
													{txn.status}
												</span>
											</p>
										{/if}
									</div>
									<div class="flex items-center gap-4">
										<span
											class="text-sm font-medium {txn.amount.amount < 0 ? 'text-red-600' : 'text-green-600'}"
										>
											<MoneyDisplay amount={txn.amount.amount} currency={txn.amount.currencyCode} />
										</span>
										{#if editTxnId !== txn.id}
											<div class="hidden gap-2 group-hover:flex">
												<button
													onclick={() => startEditTxn(txn)}
													class="text-xs text-gray-400 hover:text-gray-600"
												>
													Edit
												</button>
												<button
													onclick={() => handleDeleteTxn(txn.id)}
													class="text-xs text-red-400 hover:text-red-600"
												>
													Delete
												</button>
											</div>
										{/if}
									</div>
								</div>
							{/each}
						</div>
					{/if}
				</div>
			{:else if activeTab === 'settings'}
				<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
					<h3 class="mb-4 text-sm font-semibold text-gray-900">Account Settings</h3>
					<div class="space-y-4">
						<div class="flex items-center justify-between">
							<div>
								<p class="text-sm font-medium text-gray-700">On budget</p>
								<p class="text-xs text-gray-500">
									Include this account in budget calculations
								</p>
							</div>
							<label class="relative inline-flex cursor-pointer items-center">
								<input
									type="checkbox"
									bind:checked={editOnBudget}
									class="peer sr-only"
									onchange={saveAccountEdit}
								/>
								<div
									class="peer h-6 w-11 rounded-full bg-gray-200 peer-checked:bg-blue-600 peer-focus:ring-2 peer-focus:ring-blue-300"
								/>
							</label>
						</div>
						<div>
							<p class="text-sm font-medium text-gray-700">Currency</p>
							<p class="text-sm text-gray-900">{account.currency}</p>
						</div>
						<div>
							<p class="text-sm font-medium text-gray-700">Created</p>
							<p class="text-sm text-gray-900">{formatDate(account.createdAt)}</p>
						</div>
					</div>
				</div>
			{/if}
		{/if}
	</div>
{/if}

{#if showAddTxn}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
		<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
			<h2 class="mb-4 text-lg font-semibold text-gray-900">Add transaction</h2>
			{#if txnError}
				<div class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
					{txnError}
				</div>
			{/if}
			<div class="space-y-4">
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Date</label>
					<input
						type="date"
						bind:value={txnDate}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
					/>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Description</label>
					<input
						type="text"
						bind:value={txnDescription}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. Grocery store"
					/>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Merchant (optional)</label>
					<input
						type="text"
						bind:value={txnMerchant}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. Whole Foods"
					/>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Amount (minor units)</label>
					<input
						type="number"
						bind:value={txnAmountMinor}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. -1250 for -$12.50"
					/>
					<p class="mt-1 text-xs text-gray-500">
						Use negative for expenses. In {txnCurrency} minor units (cents for USD, satoshis for
						BTC).
					</p>
				</div>
			</div>
			<div class="mt-6 flex justify-end gap-3">
				<button
					onclick={() => (showAddTxn = false)}
					class="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200"
				>
					Cancel
				</button>
				<button
					onclick={handleAddTransaction}
					disabled={addingTxn || !txnDate || !txnDescription || !txnAmountMinor}
					class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
				>
					{addingTxn ? 'Adding…' : 'Add'}
				</button>
			</div>
		</div>
	</div>
{/if}
