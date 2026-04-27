<script lang="ts">
	import { page } from '$app/stores';
	import { auth } from '$lib/auth.svelte';
	import { goto } from '$app/navigation';
	import {
		getTransaction,
		listSplits,
		createSplit,
		updateSplit,
		deleteSplit,
		uploadTransactionAttachment,
		deleteAttachment,
		getAttachmentUrl,
		listCategories
	} from '$lib/api';
	import MoneyDisplay from '$lib/MoneyDisplay.svelte';
	import type { Transaction, TransactionSplit, Attachment, Category } from '$lib/types';

	$effect(() => {
		if (!auth.user && !auth.loading) {
			goto('/login');
		}
	});

	const transactionId = $derived($page.params.id!);

	let transaction = $state<Transaction | null>(null);
	let splits = $state<TransactionSplit[]>([]);
	let attachments = $state<Attachment[]>([]);
	let categories = $state<Category[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);

	// Split modal
	let showSplitModal = $state(false);
	let editingSplit = $state<TransactionSplit | null>(null);
	let splitAmountMinor = $state('');
	let splitCurrency = $state('USD');
	let splitCategoryId = $state('');
	let splitDescription = $state('');
	let splitMemo = $state('');
	let splitSortOrder = $state(0);
	let splitSaving = $state(false);
	let splitError = $state<string | null>(null);

	// File drop
	let dragOver = $state(false);
	let uploadError = $state<string | null>(null);
	let uploading = $state(false);

	$effect(() => {
		if (auth.user && transactionId) loadDetail();
	});

	async function loadDetail() {
		loading = true;
		error = null;
		try {
			const [txn, splitRes, catRes] = await Promise.all([
				getTransaction(transactionId),
				listSplits(transactionId),
				listCategories()
			]);
			transaction = txn;
			splits = splitRes.splits;
			attachments = (txn as any).attachments || [];
			categories = catRes.categories;
			splitCurrency = txn.amount.currencyCode;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load transaction';
		} finally {
			loading = false;
		}
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleDateString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric'
		});
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

	function openSplitModal(split: TransactionSplit | null = null) {
		editingSplit = split;
		if (split) {
			splitAmountMinor = String(Math.round(split.amount * 100));
			splitCurrency = split.currency;
			splitCategoryId = split.categoryId || '';
			splitDescription = split.description || '';
			splitMemo = split.memo || '';
			splitSortOrder = split.sortOrder;
		} else {
			splitAmountMinor = '';
			splitCurrency = transaction?.amount.currencyCode || 'USD';
			splitCategoryId = '';
			splitDescription = '';
			splitMemo = '';
			splitSortOrder = splits.length;
		}
		splitError = null;
		showSplitModal = true;
	}

	async function saveSplit() {
		if (!transaction) return;
		splitSaving = true;
		splitError = null;
		try {
			const amount = parseInt(splitAmountMinor, 10);
			if (isNaN(amount)) throw new Error('Invalid amount');
			const data = {
				amountMinor: amount,
				currency: splitCurrency,
				categoryId: splitCategoryId || undefined,
				description: splitDescription || undefined,
				memo: splitMemo || undefined,
				sortOrder: splitSortOrder
			};
			if (editingSplit) {
				await updateSplit(transactionId, editingSplit.id, data);
			} else {
				await createSplit(transactionId, data);
			}
			showSplitModal = false;
			await loadDetail();
		} catch (e) {
			splitError = e instanceof Error ? e.message : 'Failed to save split';
		} finally {
			splitSaving = false;
		}
	}

	async function removeSplit(splitId: string) {
		if (!confirm('Delete this split?')) return;
		try {
			await deleteSplit(transactionId, splitId);
			await loadDetail();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to delete split';
		}
	}

	function isImage(contentType: string): boolean {
		return contentType.startsWith('image/');
	}

	function attachmentIcon(contentType: string): string {
		if (contentType.startsWith('image/')) return '🖼️';
		if (contentType === 'application/pdf') return '📄';
		if (contentType.startsWith('text/')) return '📝';
		return '📎';
	}

	async function handleFileDrop(e: DragEvent) {
		e.preventDefault();
		dragOver = false;
		uploadError = null;
		const files = e.dataTransfer?.files;
		if (!files || files.length === 0) return;
		await uploadFiles(files);
	}

	async function handleFileSelect(e: Event) {
		const input = e.target as HTMLInputElement;
		if (!input.files || input.files.length === 0) return;
		await uploadFiles(input.files);
		input.value = '';
	}

	async function uploadFiles(files: FileList) {
		uploading = true;
		uploadError = null;
		try {
			for (const file of Array.from(files)) {
				await uploadTransactionAttachment(transactionId, file, 'receipt');
			}
			await loadDetail();
		} catch (e) {
			uploadError = e instanceof Error ? e.message : 'Upload failed';
		} finally {
			uploading = false;
		}
	}

	async function removeAttachment(id: string) {
		if (!confirm('Delete this attachment?')) return;
		try {
			await deleteAttachment(id);
			await loadDetail();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to delete attachment';
		}
	}
</script>

{#if auth.user}
	<div class="mx-auto max-w-4xl p-6">
		<header class="mb-6">
			<a href="/accounts/{transaction?.accountId}" class="text-sm text-blue-600 hover:underline"
				>← Back to account</a
			>
		</header>

		{#if loading}
			<div class="py-12 text-center text-gray-500">Loading…</div>
		{:else if error}
			<div class="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">{error}</div>
		{:else if transaction}
			<div class="mb-6 rounded-xl bg-white p-6 shadow-sm ring-1 ring-gray-100">
				<div class="flex items-start justify-between">
					<div>
						<h1 class="text-2xl font-semibold text-gray-900">{transaction.description}</h1>
						<p class="text-sm text-gray-500">
							{formatDate(transaction.occurredAt)}
							{#if transaction.merchant}
								· {transaction.merchant}
							{/if}
							<span
								class="ml-2 inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium {statusBadge(transaction.status)}"
							>
								{transaction.status}
							</span>
						</p>
					</div>
					<span
						class="text-xl font-bold {transaction.amount.amount < 0 ? 'text-red-600' : 'text-green-600'}"
					>
						<MoneyDisplay
							amount={transaction.amount.amount}
							currency={transaction.amount.currencyCode}
						/>
					</span>
				</div>
			</div>

			<!-- Splits -->
			<div class="mb-6 rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
				<div class="mb-4 flex items-center justify-between">
					<h2 class="text-lg font-semibold text-gray-900">Splits</h2>
					<button
						onclick={() => openSplitModal()}
						class="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
					>
						+ Add split
					</button>
				</div>

				{#if splits.length === 0}
					<p class="py-4 text-center text-sm text-gray-500">No splits yet.</p>
				{:else}
					<div class="divide-y divide-gray-100">
						{#each splits as split}
							<div class="flex items-center justify-between py-3">
								<div>
									<p class="text-sm font-medium text-gray-900">
										{split.description || 'Unnamed split'}
									</p>
									<p class="text-xs text-gray-500">
										{#if split.memo}
											{split.memo} ·
										{/if}
										{split.categoryId
											? categories.find((c) => c.id === split.categoryId)?.name || 'Unknown'
											: 'Uncategorized'}
									</p>
								</div>
								<div class="flex items-center gap-4">
									<span class="text-sm font-medium text-gray-900">
										<MoneyDisplay amount={split.amount} currency={split.currency} />
									</span>
									<div class="flex gap-2">
										<button
											onclick={() => openSplitModal(split)}
											class="text-xs text-gray-400 hover:text-gray-600"
										>
											Edit
										</button>
										<button
											onclick={() => removeSplit(split.id)}
											class="text-xs text-red-400 hover:text-red-600"
										>
											Delete
										</button>
									</div>
								</div>
							</div>
						{/each}
					</div>
				{/if}
			</div>

			<!-- Attachments -->
			<div class="rounded-xl bg-white p-5 shadow-sm ring-1 ring-gray-100">
				<h2 class="mb-4 text-lg font-semibold text-gray-900">Attachments</h2>

				{#if uploadError}
					<div class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
						{uploadError}
					</div>
				{/if}

				<div
					class="mb-4 rounded-lg border-2 border-dashed p-6 text-center transition-colors {dragOver
						? 'border-blue-500 bg-blue-50'
						: 'border-gray-300 bg-gray-50'}"
					ondragover={(e: DragEvent) => {
						e.preventDefault();
						dragOver = true;
					}}
					ondragleave={() => (dragOver = false)}
					ondrop={handleFileDrop}
				>
					<p class="text-sm text-gray-600">
						{uploading ? 'Uploading…' : 'Drag and drop files here, or click to browse'}
					</p>
					<input
						type="file"
						class="hidden"
						multiple
						onchange={handleFileSelect}
						id="file-input"
					/>
					<label
						for="file-input"
						class="mt-2 inline-block cursor-pointer rounded-md bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-gray-300 hover:bg-gray-50"
					>
						Browse
					</label>
				</div>

				{#if attachments.length === 0}
					<p class="py-4 text-center text-sm text-gray-500">No attachments yet.</p>
				{:else}
					<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
						{#each attachments as att}
							<div class="rounded-lg border border-gray-200 p-3">
								{#if isImage(att.contentType)}
									<img
										src={getAttachmentUrl(att.id)}
										alt="Attachment"
										class="mb-2 h-32 w-full rounded object-cover"
									/>
								{:else}
									<div class="mb-2 flex h-32 items-center justify-center rounded bg-gray-100 text-4xl">
										{attachmentIcon(att.contentType)}
									</div>
								{/if}
								<div class="flex items-center justify-between">
									<div class="min-w-0">
										<p class="truncate text-xs font-medium text-gray-700">{att.kind}</p>
										<p class="text-xs text-gray-500">{(att.sizeBytes / 1024).toFixed(1)} KB</p>
									</div>
									<button
										onclick={() => removeAttachment(att.id)}
										class="ml-2 text-xs text-red-400 hover:text-red-600"
									>
										Delete
									</button>
								</div>
							</div>
						{/each}
					</div>
				{/if}
			</div>
		{/if}
	</div>
{/if}

{#if showSplitModal}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
		<div class="w-full max-w-md rounded-xl bg-white p-6 shadow-lg">
			<h2 class="mb-4 text-lg font-semibold text-gray-900">
				{editingSplit ? 'Edit split' : 'Add split'}
			</h2>
			{#if splitError}
				<div class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
					{splitError}
				</div>
			{/if}
			<div class="space-y-4">
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Amount (minor units)</label>
					<input
						type="number"
						bind:value={splitAmountMinor}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. -1250 for -$12.50"
					/>
					<p class="mt-1 text-xs text-gray-500">
						In {splitCurrency} minor units (cents for USD).
					</p>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Category</label>
					<select
						bind:value={splitCategoryId}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
					>
						<option value="">Uncategorized</option>
						{#each categories as cat}
							<option value={cat.id}>{cat.name}</option>
						{/each}
					</select>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Description</label>
					<input
						type="text"
						bind:value={splitDescription}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. Coffee"
					/>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium text-gray-700">Memo (optional)</label>
					<input
						type="text"
						bind:value={splitMemo}
						class="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
						placeholder="e.g. Morning meeting"
					/>
				</div>
			</div>
			<div class="mt-6 flex justify-end gap-3">
				<button
					onclick={() => (showSplitModal = false)}
					class="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200"
				>
					Cancel
				</button>
				<button
					onclick={saveSplit}
					disabled={splitSaving || !splitAmountMinor}
					class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
				>
					{splitSaving ? 'Saving…' : 'Save'}
				</button>
			</div>
		</div>
	</div>
{/if}
