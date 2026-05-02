<script lang="ts">
	import { onMount } from 'svelte';
	import { clearCookie } from '$lib/api';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';

	onMount(async () => {
		try {
			await clearCookie();
		} catch {
			// ignore
		}
		auth.clear();
		goto('/portal/login');
	});
</script>

<div class="flex min-h-screen items-center justify-center">
	<p class="text-gray-600">Signing out…</p>
</div>
