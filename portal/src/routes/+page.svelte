<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import { listAccounts, getBalance, listTransactions, listConnections, getOnboarding } from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Account, Transaction, DataFeedConnection } from '$lib/types';
	import type { OnboardingState } from '$lib/api';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	let accounts = $state<Account[]>([]);
	let balances = $state<Record<string, { posted: number; currency: string }>>({});
	let recentTxns = $state<Transaction[]>([]);
	let connections = $state<DataFeedConnection[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let onboarding = $state<OnboardingState | null>(null);
	let showChecklist = $state(false);

	$effect(() => {
		if (auth.user) {
			loadDashboard();
		}
	});

	async function loadDashboard() {
		loading = true;
		error = null;
		try {
			const [acctRes, txnRes, connRes, onb] = await Promise.all([
				listAccounts(),
				listTransactions({ limit: 10 }),
				listConnections(),
				getOnboarding().catch(() => null)
			]);
			onboarding = onb;
			accounts = acctRes.accounts;
			recentTxns = txnRes.transactions;
			connections = connRes.connections;

			// Load balances for all accounts
			const balanceResults = await Promise.all(
				accounts.map((a) =>
					getBalance(a.id)
						.then((b) => ({ id: a.id, posted: b.posted.amount, currency: b.posted.currencyCode }))
						.catch(() => ({ id: a.id, posted: 0, currency: a.currency }))
				)
			);
			balances = Object.fromEntries(balanceResults.map((b) => [b.id, b]));
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load dashboard';
		} finally {
			loading = false;
		}
	}

	const onBudgetAccounts = $derived(accounts.filter((a) => a.isOnBudget && a.isActive));
	const offBudgetAccounts = $derived(accounts.filter((a) => !a.isOnBudget && a.isActive));
	const netWorth = $derived(
		onBudgetAccounts.reduce((sum, a) => {
			const b = balances[a.id];
			return sum + (b?.posted ?? 0);
		}, 0)
	);
	const failingConnections = $derived(
		connections.filter(
			(c) => c.status.type === 'NeedsReauth' || c.status.type === 'Error'
		)
	);

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
				<h1 class="text-2xl font-semibold text-gray-900">Steward</h1>
				<p class="text-sm text-gray-500">Welcome back, {auth.user.displayName}</p>
			</div>
			<div class="flex items-center gap-4">
				<a href="/budgets" class="text-sm text-gray-600 hover:text-gray-900">Budgets</a>
				<a href="/reconciliation" class="text-sm text-gray-600 hover:text-gray-900">Reconcile</a>
				<a href="/logout" class="text-sm text-blue-600 hover:underline">Log out</a>
			</div>
		</header>

		{#if onboarding && onboarding.currentStep < 5}
			<div class="mb-6 rounded-lg border border-blue-200 bg-blue-50 p-4">
				<div class="flex items-center justify-between">
					<p class="text-sm font-medium text-blue-800">Getting started</p>
					<a href="/welcome" class="text-xs text-blue-600 hover:underline">Continue setup</a>
				</div>
				<ul class="mt-2 space-y-1">
					<li class="flex items-center gap-2 text-xs text-blue-700">
						<span class="text-green-600">✓</span> Create account
					</li>
					<li class="flex items-center gap-2 text-xs text-blue-700">
						<span class="text-green-600">✓</span> Create tenant
					</li>
					<li class="flex items-center gap-2 text-xs {connections.length > 0 ? 'text-blue-700' : 'text-blue-600'}">
						<span class="{connections.length > 0 ? 'text-green-600' : 'text-blue-400'}">{connections.length > 0 ? '✓' : '○'}</span>
						Link a bank account
					</li>
					<li class="flex items-center gap-2 text-xs {recentTxns.length > 0 ? 'text-blue-700' : 'text-blue-600'}">
						<span class="{recentTxns.length > 0 ? 'text-green-600' : 'text-blue-400'}">{recentTxns.length > 0 ? '✓' : '○'}</span>
						First transaction
					</li>
					<li class="flex items-center gap-2 text-xs text-blue-600">
						<span class="text-blue-400">○</span> Create a budget
					</li>
				</ul>
			</div>
		{/if}

		{#if failingConnections.length > 0}
			<div class="mb-6 rounded-lg border border-amber-200 bg-amber-50 p-4">
				<p class="text-sm font-medium text-amber-800">
					⚠️ {failingConnections.length} connection{failingConnections.length > 1 ? 's' : ''} need attention
				</p>
				<ul class="mt-2 space-y-1">
					{#each failingConnections as conn}
						<li class="text-xs text-amber-700">
							{conn.institutionName || conn.provider} —
							{#if conn.status.type === 'NeedsReauth'}
								Needs reauthorization
							{:else if conn.status.type === 'Error'}
								Error{conn.status.type === 'Error' && 'message' in conn.status ? `: ${conn.status.message}` : ''}
							{/if}
						</li>
					{/each}
				</ul>
			</div>
		{/if}

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else}
			<!-- Net Worth -->
			<div class="mb-8 rounded-xl bg-white p-6 shadow-sm ring-1 ring-gray-100">
				<p class="text-sm font-medium text-gray-500">Net worth (on-budget)</p>
				<p class="mt-1 text-3xl font-bold text-gray-900">
					<MoneyDisplay amount={netWorth} currency="USD" />
				</p>
			</div>

			<!-- On-budget accounts -->
			<div class="mb-8">
				<div class="mb-4 flex items-center justify-between">
					<h2 class="text-lg font-semibold text-gray-900">Accounts</h2>
					<a
						href="/accounts"
						class="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
					>
						Manage accounts
					</a>
				</div>
				<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
					{#each onBudgetAccounts as account}
						<a
							href="/accounts/{account.id}"
							class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100 transition hover:shadow-md"
						>
							<div class="flex items-start justify-between">
								<div>
									<p class="text-sm font-medium text-gray-900">{account.name}</p>
									<p class="text-xs text-gray-500">{accountTypeLabel(account.accountType)}</p>
								</div>
								<span
									class="inline-flex items-center rounded-full bg-green-50 px-2 py-0.5 text-xs font-medium text-green-700"
								>
									On budget
								</span>
							</div>
							<p class="mt-4 text-xl font-semibold text-gray-900">
								{#if balances[account.id]}
									<MoneyDisplay
										amount={balances[account.id].posted}
										currency={balances[account.id].currency}
									/>
								{:else}
									—
								{/if}
							</p>
						</a>
					{/each}
				</div>
			</div>

			<!-- Recent transactions -->
			<div class="rounded-xl bg-white p-6 shadow-sm ring-1 ring-gray-100">
				<h2 class="mb-4 text-lg font-semibold text-gray-900">Recent transactions</h2>
				{#if recentTxns.length === 0}
					<p class="text-sm text-gray-500">No transactions yet.</p>
				{:else}
					<div class="divide-y divide-gray-100">
						{#each recentTxns as txn}
							<div class="flex items-center justify-between py-3">
								<div>
									<p class="text-sm font-medium text-gray-900">{txn.description}</p>
									<p class="text-xs text-gray-500">
										{formatDate(txn.occurredAt)}
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
						{/each}
					</div>
				{/if}
			</div>
		{/if}
	</div>
{/if}
