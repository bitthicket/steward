<script lang="ts">
	import './layout.css';
	import { auth } from '$lib/auth.svelte';

	let { children } = $props();
</script>

{#if auth.loading}
	<div class="flex min-h-screen items-center justify-center">
		<div class="text-center">
			<div class="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-2 border-gray-300 border-t-blue-600"></div>
			<p class="text-gray-600">Loading…</p>
		</div>
	</div>
{:else}
	{@render children()}
{/if}
