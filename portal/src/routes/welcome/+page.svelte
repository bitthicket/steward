<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import { getOnboarding, patchOnboarding, createBudget, listConnections } from '$lib/api';
	let step = $state(3);
	let loading = $state(true);
	let error = $state('');
	let submitting = $state(false);

	// Step 3: Plaid Link
	let plaidLinkToken = $state('');
	let plaidReady = $state(false);
	let connectionsCount = $state(0);

	// Step 4: Budget
	let budgetStyle = $state('ZeroBased');
	let budgetName = $state('Monthly Budget');
	let budgetCurrency = $state('USD');

	$effect(() => {
		if (!auth.loading) {
			if (!auth.user) {
				goto('/login');
			} else {
				loadOnboarding();
			}
		}
	});

	async function loadOnboarding() {
		loading = true;
		try {
			const state = await getOnboarding();
			if (state.currentStep >= 5) {
				goto('/portal');
				return;
			}
			step = state.currentStep;
			// Check if connections exist
			const connRes = await listConnections();
			connectionsCount = connRes.connections.length;
			if (connectionsCount > 0 && step === 3) {
				step = 4;
			}
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load onboarding';
		} finally {
			loading = false;
		}
	}

	async function initPlaid() {
		try {
			const res = await fetch('/api/connections/plaid/link-token', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				credentials: 'include'
			});
			if (!res.ok) {
				throw new Error('Plaid is not configured');
			}
			const data = await res.json();
			plaidLinkToken = data.linkToken;
			loadPlaidScript();
		} catch (e) {
			// Plaid not configured — show skip option
			plaidReady = false;
		}
	}

	function loadPlaidScript() {
		if (document.getElementById('plaid-script')) {
			plaidReady = true;
			return;
		}
		const script = document.createElement('script');
		script.id = 'plaid-script';
		script.src = 'https://cdn.plaid.com/link/v2/stable/link-initialize.js';
		script.onload = () => {
			plaidReady = true;
		};
		script.onerror = () => {
			plaidReady = false;
		};
		document.head.appendChild(script);
	}

	function openPlaidLink() {
		// @ts-ignore
		const handler = window.Plaid.create({
			token: plaidLinkToken,
			onSuccess: async (publicToken: string, metadata: any) => {
				const res = await fetch('/api/connections/plaid/exchange', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					credentials: 'include',
					body: JSON.stringify({
						publicToken,
						institutionId: metadata.institution?.institution_id || '',
						institutionName: metadata.institution?.name || ''
					})
				});
				if (!res.ok) {
					error = 'Failed to exchange Plaid token';
					return;
				}
				connectionsCount = 1;
				await patchOnboarding({ currentStep: 4, completedSteps: [1, 2, 3], skipped: false });
				step = 4;
			},
			onExit: () => {}
		});
		handler.open();
	}

	async function skipFeed() {
		submitting = true;
		try {
			await patchOnboarding({ currentStep: 4, completedSteps: [1, 2, 3], skipped: true });
			step = 4;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to skip';
		} finally {
			submitting = false;
		}
	}

	async function createInitialBudget() {
		submitting = true;
		try {
			await createBudget({
				name: budgetName,
				period: 'monthly',
				currency: budgetCurrency,
				style: budgetStyle,
				income: 0
			});
			await patchOnboarding({ currentStep: 5, completedSteps: [1, 2, 3, 4], skipped: false });
			step = 5;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to create budget';
		} finally {
			submitting = false;
		}
	}

	async function skipBudget() {
		submitting = true;
		try {
			await patchOnboarding({ currentStep: 5, completedSteps: [1, 2, 3, 4], skipped: true });
			step = 5;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to skip';
		} finally {
			submitting = false;
		}
	}

	function finish() {
		goto('/portal');
	}

	$effect(() => {
		if (step === 3 && auth.user) {
			initPlaid();
		}
	});
</script>

{#if auth.user}
	<div class="flex min-h-screen items-center justify-center bg-gray-50 px-4">
		<div class="w-full max-w-md rounded-lg bg-white p-8 shadow">
			{#if loading}
				<div class="py-8 text-center text-gray-500">Loading…</div>
			{:else}
				<!-- Progress indicator -->
				<div class="mb-6 flex gap-1">
					{#each [1, 2, 3, 4, 5] as s}
						<div class="h-1 flex-1 rounded-full {s <= step ? 'bg-blue-600' : 'bg-gray-200'}"></div>
					{/each}
				</div>

				{#if error}
					<div class="mb-4 rounded-md bg-red-50 p-3 text-sm text-red-700">{error}</div>
				{/if}

				{#if step === 1}
					<h1 class="mb-2 text-2xl font-semibold text-gray-900">Welcome to Steward</h1>
					<p class="mb-6 text-sm text-gray-600">
						Let's get your finances organized in a few quick steps.
					</p>
					<button
						onclick={() => step = 2}
						class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
					>
						Get started
					</button>
				{:else if step === 2}
					<h1 class="mb-2 text-2xl font-semibold text-gray-900">Create your first account</h1>
					<p class="mb-6 text-sm text-gray-600">
						You can add a manual account now or connect a bank account in the next step.
					</p>
					<button
						onclick={() => step = 3}
						class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
					>
						Continue
					</button>
				{:else if step === 3}
					<h1 class="mb-2 text-2xl font-semibold text-gray-900">Connect your bank</h1>
					<p class="mb-6 text-sm text-gray-600">
						Link a bank account to automatically import transactions. You can skip this and add accounts manually later.
					</p>

					{#if plaidReady}
						<button
							onclick={openPlaidLink}
							class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
						>
							Connect with Plaid
						</button>
					{:else}
						<p class="mb-4 text-sm text-amber-700">
							Plaid Link is not available right now.
						</p>
					{/if}

					<button
						onclick={skipFeed}
						disabled={submitting}
						class="mt-3 w-full rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
					>
						{submitting ? 'Skipping…' : "I'll do this later"}
					</button>
				{:else if step === 4}
					<h1 class="mb-2 text-2xl font-semibold text-gray-900">Set your budget</h1>
					<p class="mb-6 text-sm text-gray-600">
						Choose a budgeting style. A monthly budget will be created with default categories at zero allocation.
					</p>

					<div class="mb-4">
						<label class="mb-1 block text-sm font-medium text-gray-700">Budget name</label>
						<input
							type="text"
							bind:value={budgetName}
							class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
						/>
					</div>

					<div class="mb-4">
						<label class="mb-1 block text-sm font-medium text-gray-700">Budgeting style</label>
						<select
							bind:value={budgetStyle}
							class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
						>
							<option value="ZeroBased">Zero-based (envelope)</option>
							<option value="Envelope">Envelope system</option>
							<option value="Flexible">Flexible</option>
							<option value="TraditionalLimits">Traditional limits</option>
						</select>
					</div>

					<div class="mb-6">
						<label class="mb-1 block text-sm font-medium text-gray-700">Currency</label>
						<select
							bind:value={budgetCurrency}
							class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
						>
							<option value="USD">USD</option>
							<option value="EUR">EUR</option>
							<option value="GBP">GBP</option>
							<option value="BTC">BTC</option>
						</select>
					</div>

					<button
						onclick={createInitialBudget}
						disabled={submitting}
						class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
					>
						{submitting ? 'Creating…' : 'Create budget'}
					</button>

					<button
						onclick={skipBudget}
						disabled={submitting}
						class="mt-3 w-full rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
					>
						{submitting ? 'Skipping…' : "Skip for now"}
					</button>
				{:else if step === 5}
					<h1 class="mb-2 text-2xl font-semibold text-gray-900">You're all set!</h1>
					<p class="mb-6 text-sm text-gray-600">
						Your dashboard is ready. You can connect accounts and refine your budget any time.
					</p>
					<button
						onclick={finish}
						class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
					>
						Open dashboard
					</button>
				{/if}
			{/if}
		</div>
	</div>
{/if}
