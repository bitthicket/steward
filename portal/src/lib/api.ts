import type {
	User,
	Membership,
	Account,
	Balance,
	Transaction,
	DataFeedConnection,
	Category,
	Budget,
	BudgetCurrentPeriod,
	BudgetReport,
	Reconciliation,
	ReconciliationWithTransactions
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

// ── Categories ─────────────────────────────────────────────────────────────

export async function listCategories() {
	return api<{ categories: Category[] }>('/api/categories');
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

export async function listBudgets() {
	return api<{ budgets: Budget[] }>('/api/budgets');
}

export async function getBudget(id: string) {
	return api<Budget>(`/api/budgets/${id}`);
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

export async function createPeriod(
	budgetId: string,
	data: { startDate: string; allocations: { categoryId: string; amountMinor: number }[] }
) {
	return api<BudgetCurrentPeriod>(`/api/budgets/${budgetId}/periods`, {
		method: 'POST',
		body: JSON.stringify(data)
	});
}

export async function updateAllocation(
	budgetId: string,
	periodId: string,
	categoryId: string,
	data: { amountMinor: number; rolloverEnabled?: boolean }
) {
	return api<{ success: boolean }>(
		`/api/budgets/${budgetId}/periods/${periodId}/categories/${categoryId}`,
		{
			method: 'PATCH',
			body: JSON.stringify(data)
		}
	);
}

export async function closePeriod(budgetId: string, periodId: string) {
	return api<{
		closedPeriodId: string;
		nextPeriodId: string;
		rolloverBalances: { categoryId: string; rolloverAmountMinor: number; currency: string }[];
	}>(`/api/budgets/${budgetId}/periods/${periodId}/close`, { method: 'POST' });
}

export async function getBudgetReport(budgetId: string, periodId: string, displayCurrency?: string) {
	const qs = displayCurrency ? `?displayCurrency=${displayCurrency}` : '';
	return api<BudgetReport>(`/api/budgets/${budgetId}/periods/${periodId}/report${qs}`);
}

export async function getCurrentBudgetReport(budgetId: string, displayCurrency?: string) {
	const qs = displayCurrency ? `?displayCurrency=${displayCurrency}` : '';
	return api<BudgetReport>(`/api/budgets/${budgetId}/periods/current/report${qs}`);
}

// ── Reconciliations ────────────────────────────────────────────────────────

export async function listReconciliations() {
	return api<{ reconciliations: Reconciliation[] }>('/api/reconciliations');
}

export async function getReconciliation(id: string) {
	return api<ReconciliationWithTransactions>(`/api/reconciliations/${id}`);
}

export async function createReconciliation(data: {
	accountId: string;
	statementDate: string;
	statementBalanceMinor: number;
	currency: string;
}) {
	return api<{ reconciliation: Reconciliation; candidateTransactions: Transaction[] }>(
		'/api/reconciliations',
		{
			method: 'POST',
			body: JSON.stringify(data)
		}
	);
}

export async function updateReconciliationTransactions(
	id: string,
	data: { included: string[]; excluded: string[] }
) {
	return api<{ success: boolean }>(`/api/reconciliations/${id}/transactions`, {
		method: 'PATCH',
		body: JSON.stringify(data)
	});
}

export async function completeReconciliation(id: string, force = false) {
	return api<{ status: string; diffMinor: number }>(
		`/api/reconciliations/${id}/complete?force=${force}`,
		{ method: 'POST' }
	);
}

export async function abortReconciliation(id: string) {
	return api<{ status: string }>(`/api/reconciliations/${id}/abort`, { method: 'POST' });
}

