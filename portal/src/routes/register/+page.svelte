<script lang="ts">
	import { register, setCookie } from '$lib/api';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';

	let email = $state('');
	let password = $state('');
	let displayName = $state('');
	let tenantDisplayName = $state('');
	let error = $state('');
	let submitting = $state(false);

	async function handleSubmit(e: Event) {
		e.preventDefault();
		error = '';
		submitting = true;
		try {
			const result = await register({
				email,
				password,
				displayName: displayName || undefined,
				tenantDisplayName
			});
			await setCookie(result.accessToken);
			await auth.refresh();
			goto('/welcome');
		} catch (e) {
			error = e instanceof Error ? e.message : 'Registration failed';
		} finally {
			submitting = false;
		}
	}
</script>

<div class="flex min-h-screen items-center justify-center bg-gray-50 px-4">
	<div class="w-full max-w-sm rounded-lg bg-white p-8 shadow">
		<h1 class="mb-6 text-center text-2xl font-semibold text-gray-900">Create account</h1>
		<form onsubmit={handleSubmit} class="space-y-4">
			<div>
				<label for="email" class="mb-1 block text-sm font-medium text-gray-700">Email</label>
				<input id="email" type="email" bind:value={email} required class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
			</div>
			<div>
				<label for="displayName" class="mb-1 block text-sm font-medium text-gray-700">Display name</label>
				<input id="displayName" type="text" bind:value={displayName} class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
			</div>
			<div>
				<label for="tenant" class="mb-1 block text-sm font-medium text-gray-700">Tenant name</label>
				<input id="tenant" type="text" bind:value={tenantDisplayName} required class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
			</div>
			<div>
				<label for="password" class="mb-1 block text-sm font-medium text-gray-700">Password</label>
				<input id="password" type="password" bind:value={password} required minlength="8" class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
			</div>
			{#if error}
				<p class="text-sm text-red-600">{error}</p>
			{/if}
			<button type="submit" disabled={submitting} class="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
				{submitting ? 'Creating…' : 'Create account'}
			</button>
		</form>
		<p class="mt-4 text-center text-sm text-gray-600">
			Already have an account? <a href="/portal/login" class="text-blue-600 hover:underline">Sign in</a>
		</p>
	</div>
</div>
