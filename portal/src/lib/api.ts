import type {
	User,
	Membership,
	Account,
	Balance,
	Transaction,
	TransactionSplit,
	Attachment,
	DataFeedConnection,
	Category
} from './types';

const API_BASE = '';

async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		...options,
		headers: {
			'Content-Type': 'application/json',
			...options.headers
		},
		credentials: 'include'
	});
	if (!res.ok) {
		const body = await res.json().catch(() => ({}));
		throw new ApiError(res.status, body.error || `HTTP ${res.status}`);
	}
	return res.json();
}

export class ApiError extends Error {
	constructor(public status: number, message: string) {
		super(message);
	}
}

export async function register(data: {
	email: string;
	password: string;
	displayName?: string;
	tenantDisplayName: string;
}) {
	return api<{ userId: string; tenantId: string; accessToken: string }>('/auth/register', {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function login(data: { email: string; password: string; tenantId?: string }) {
	return api<{ accessToken?: string; memberships?: Membership[] }>('/auth/login', {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function setCookie(accessToken: string) {
	return api<void>('/api/auth/cookie-set', {
		method: 'POST',
		body: JSON.stringify({ accessToken })
	});
}

export async function me() {
	return api<User>('/me');
}

export async function clearCookie() {
	return api<void>('/api/auth/cookie-set', {
		method: 'POST',
		body: JSON.stringify({ accessToken: '' })
	});
}

// ── Accounts ───────────────────────────────────────────────────────────────

export async function listAccounts() {
	return api<{ accounts: Account[] }>('/api/accounts');
}

export async function getAccount(id: string) {
	return api<Account>(`/api/accounts/${id}`);
}

export async function createAccount(data: {
	name: string;
	accountType: string;
	currency: string;
	isOnBudget?: boolean;
	institutionName?: string;
	externalId?: string;
}) {
	return api<Account>('/api/accounts', {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function updateAccount(
	id: string,
	data: { name?: string; isOnBudget?: boolean }
) {
	return api<Account>(`/api/accounts/${id}`, {
		method: 'PATCH',
		body: JSON.stringify(data)
	});
}

export async function deleteAccount(id: string) {
	return api<void>(`/api/accounts/${id}`, { method: 'DELETE' });
}

export async function getBalance(id: string, displayCurrency?: string) {
	const qs = displayCurrency ? `?displayCurrency=${displayCurrency}` : '';
	return api<Balance>(`/api/accounts/${id}/balance${qs}`);
}

// ── Transactions ───────────────────────────────────────────────────────────

export async function listTransactions(params?: {
	accountId?: string;
	from?: string;
	to?: string;
	status?: string;
	limit?: number;
	cursor?: string;
}) {
	const search = new URLSearchParams();
	if (params?.accountId) search.set('accountId', params.accountId);
	if (params?.from) search.set('from', params.from);
	if (params?.to) search.set('to', params.to);
	if (params?.status) search.set('status', params.status);
	if (params?.limit) search.set('limit', String(params.limit));
	if (params?.cursor) search.set('cursor', params.cursor);
	const qs = search.toString();
	return api<{ transactions: Transaction[]; nextCursor?: string }>(
		`/api/transactions${qs ? '?' + qs : ''}`
	);
}

export async function getTransaction(id: string) {
	return api<Transaction>(`/api/transactions/${id}`);
}

export async function createTransaction(data: {
	accountId: string;
	occurredAt: string;
	postedAt?: string;
	amountMinor: number;
	currency: string;
	description: string;
	merchant?: string;
	categoryId?: string;
	transferAccountId?: string;
}) {
	return api<Transaction>('/api/transactions', {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function updateTransaction(
	id: string,
	data: {
		description?: string;
		merchant?: string;
		categoryId?: string;
		memo?: string;
	}
) {
	return api<Transaction>(`/api/transactions/${id}`, {
		method: 'PATCH',
		body: JSON.stringify(data)
	});
}

export async function deleteTransaction(id: string) {
	return api<void>(`/api/transactions/${id}`, { method: 'DELETE' });
}

// ── Splits ─────────────────────────────────────────────────────────────────

export async function listSplits(transactionId: string) {
	return api<{ splits: TransactionSplit[] }>(`/api/transactions/${transactionId}/splits`);
}

export async function createSplit(
	transactionId: string,
	data: {
		amountMinor: number;
		currency: string;
		categoryId?: string;
		description?: string;
		memo?: string;
		sortOrder?: number;
	}
) {
	return api<TransactionSplit>(`/api/transactions/${transactionId}/splits`, {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function updateSplit(
	transactionId: string,
	splitId: string,
	data: {
		amountMinor?: number;
		currency?: string;
		categoryId?: string | null;
		description?: string | null;
		memo?: string | null;
		sortOrder?: number;
	}
) {
	return api<TransactionSplit>(`/api/transactions/${transactionId}/splits/${splitId}`, {
		method: 'PATCH',
		body: JSON.stringify(data)
	});
}

export async function deleteSplit(transactionId: string, splitId: string) {
	return api<void>(`/api/transactions/${transactionId}/splits/${splitId}`, { method: 'DELETE' });
}

// ── Attachments ────────────────────────────────────────────────────────────

export async function uploadTransactionAttachment(
	transactionId: string,
	file: File,
	kind: string = 'other'
) {
	const form = new FormData();
	form.append('file', file);
	form.append('kind', kind);
	return api<Attachment>(`/api/transactions/${transactionId}/attachments`, {
		method: 'POST',
		body: form
	});
}

export async function uploadSplitAttachment(
	transactionId: string,
	splitId: string,
	file: File,
	kind: string = 'other'
) {
	const form = new FormData();
	form.append('file', file);
	form.append('kind', kind);
	return api<Attachment>(`/api/transactions/${transactionId}/splits/${splitId}/attachments`, {
		method: 'POST',
		body: form
	});
}

export function getAttachmentUrl(attachmentId: string) {
	return `/api/attachments/${attachmentId}`;
}

export async function deleteAttachment(attachmentId: string) {
	return api<void>(`/api/attachments/${attachmentId}`, { method: 'DELETE' });
}

// ── Connections ────────────────────────────────────────────────────────────

export async function listConnections() {
	return api<{ connections: DataFeedConnection[] }>('/api/connections');
}

export async function listCategories() {
	return api<{ categories: Category[] }>('/api/categories');
}

