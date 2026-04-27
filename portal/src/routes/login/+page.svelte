<script lang="ts">
	import { login, setCookie } from '$lib/api';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import type { Membership } from '$lib/types';

	let email = $state('');
	let password = $state('');
	let error = $state('');
	let submitting = $state(false);
	let memberships = $state<Membership[] | null>(null);

	async function handleSubmit(e: Event) {
		e.preventDefault();
		error = '';
		submitting = true;
		try {
			const result = await login({ email, password });
			if (result.memberships) {
				memberships = result.memberships;
			} else if (result.accessToken) {
				await setCookie(result.accessToken);
				await auth.refresh();
				goto('/portal');
			}
		} catch (e) {
			error = e instanceof Error ? e.message : 'Sign in failed';
		} finally {
			submitting = false;
		}
	}

	async function pickTenant(tenantId: string) {
		error = '';
		submitting = true;
		try {
			const result = await login({ email, password, tenantId });
			if (result.accessToken) {
				await setCookie(result.accessToken);
				await auth.refresh();
				goto('/portal');
			}
		} catch (e) {
			error = e instanceof Error ? e.message : 'Sign in failed';
		} finally {
			submitting = false;
		}
	}
</script>

<div class="flex min-h-screen items-center justify-center bg-gray-50 px-4">
	<div class="w-full max-w-sm rounded-lg bg-white p-8 shadow">
		<h1 class="mb-6 text-center text-2xl font-semibold text-gray-900">Sign in</h1>
		{#if memberships}
			<p class="mb-4 text-sm text-gray-600">Choose a tenant to continue:</p>
			<div class="space-y-2">
				{#each memberships as m}
					<button
						onclick={() => pickTenant(m.tenantId)}
						disabled={submitting}
						class="w-full rounded-md border border-gray-200 px-4 py-3 text-left text-sm hover:bg-gray-50 disabled:opacity-50"
					>
						<span class="font-medium text-gray-900">{m.tenantDisplayName}</span>
						<span class="ml-2 text-xs text-gray-500">{m.role}</span>
					</button>
				{/each}
			</div>
			{#if error}
				<p class="mt-3 text-sm text-red-600">{error}</p>
			{/if}
		{:else}
			<form onsubmit={handleSubmit} class="space-y-4">
				<div>
					<label for="email" class="mb-1 block text-sm font-medium text-gray-700">Email</label>
					<input id="email" type="email" bind:value={email} required class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
				</div>
				<div>
					<label for="password" class="mb-1 block text-sm font-medium text-gray-700">Password</label>
					<input id="password" type="password" bind:value={password} required class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
				</div>
				{#if error}
					<p class="text-sm text-red-600">{error}</p>
				{/if}
				<button type="submit" disabled={submitting} class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
					{submitting ? 'Signing in…' : 'Sign in'}
				</button>
			</form>
			<p class="mt-4 text-center text-sm text-gray-600">
				Need an account? <a href="/portal/register" class="text-blue-600 hover:underline">Create one</a>
			</p>
		{/if}
	</div>
</div>
