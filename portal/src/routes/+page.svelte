<script lang="ts">
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/portal/login');
		}
	});
</script>

{#if auth.user}
	<div class="mx-auto max-w-4xl p-6">
		<header class="mb-8 flex items-center justify-between">
			<h1 class="text-2xl font-semibold text-gray-900">Steward</h1>
			<a href="/portal/logout" class="text-sm text-blue-600 hover:underline">Log out</a>
		</header>
		<main>
			<h2 class="text-xl text-gray-800">Welcome, {auth.user.displayName}</h2>
			<p class="mt-2 text-gray-600">Your dashboard will be here soon.</p>
		</main>
	</div>
{/if}
