import type {
	User,
	Membership,
	Account,
	Balance,
	Transaction,
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

// ── Connections ────────────────────────────────────────────────────────────

export async function listConnections() {
	return api<{ connections: DataFeedConnection[] }>('/api/connections');
}

// ── Onboarding ─────────────────────────────────────────────────────────────

export interface OnboardingState {
	tenantId: string;
	currentStep: number;
	startedAt: string;
	completedAt: string | null;
	completedSteps: number[];
	skipped: boolean;
}

export async function getOnboarding() {
	return api<OnboardingState>('/api/onboarding');
}

export async function patchOnboarding(data: {
	currentStep: number;
	completedSteps: number[];
	skipped: boolean;
}) {
	return api<{ status: string }>('/api/onboarding', {
		method: 'PATCH',
		body: JSON.stringify(data)
	});
}

// ── Budgets ────────────────────────────────────────────────────────────────

export interface Budget {
	id: string;
	name: string;
	period: string;
	currency: string;
	style: string;
	income: { amount: number; currencyCode: string } | null;
	startsOn: string;
	isActive: boolean;
}

export async function createBudget(data: {
	name: string;
	period: string;
	currency: string;
	style: string;
	income?: number;
	startsOn?: string;
}) {
	return api<Budget>('/api/budgets', {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

