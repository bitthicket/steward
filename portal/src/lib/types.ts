export interface User {
	userId: string;
	tenantId: string;
	role: string;
	email: string;
	displayName: string;
}

export interface Membership {
	tenantId: string;
	tenantDisplayName: string;
	role: string;
}

export interface Money {
	amount: number;
	currencyCode: string;
}

export interface Account {
	id: string;
	name: string;
	accountType: string;
	currency: string;
	institutionName: string | null;
	externalId: string | null;
	isOnBudget: boolean;
	isActive: boolean;
	createdAt: string;
	updatedAt: string;
}

export interface Balance {
	posted: Money;
	available: Money;
	pending: Money;
	displayCurrency: string | null;
	converted: {
		posted: Money;
		available: Money;
		pending: Money;
	} | null;
}

export type TransactionStatus = 'Pending' | 'NeedsReview' | 'Cleared' | 'Reconciled';

export interface Transaction {
	id: string;
	accountId: string;
	occurredAt: string;
	postedAt: string | null;
	amount: Money;
	description: string;
	merchant: string | null;
	memo: string | null;
	categoryId: string | null;
	status: TransactionStatus;
	matchedTransactionId: string | null;
	transferAccountId: string | null;
	createdAt: string;
	updatedAt: string;
}

export interface TransactionSplit {
	id: string;
	transactionId: string;
	amount: number;
	currency: string;
	categoryId: string | null;
	description: string | null;
	memo: string | null;
	source: string;
	sortOrder: number;
	createdAt: string;
	updatedAt: string;
}

export interface Attachment {
	id: string;
	transactionId: string;
	splitId: string | null;
	kind: string;
	contentType: string;
	sizeBytes: number;
	uploadedAt: string;
}

export type ConnectionStatus =
	| { type: 'Active' }
	| { type: 'NeedsReauth' }
	| { type: 'Disabled' }
	| { type: 'Error'; message: string };

export interface DataFeedConnection {
	id: string;
	provider: string;
	status: ConnectionStatus;
	institutionName: string | null;
	createdAt: string;
	updatedAt: string;
}

export interface Category {
	id: string;
	name: string;
	color: string | null;
}

// ── Budgets ────────────────────────────────────────────────────────────────

export type BudgetPeriod = 'monthly' | 'biweekly' | 'weekly';
export type BudgetStyle = 'zeroBased' | 'envelope' | 'flexible' | 'traditionalLimits';

export interface BudgetPeriodCategory {
	categoryId: string;
	allocatedMinor: number;
	openingBalanceMinor: number;
	rolloverBalanceMinor: number;
	currency: string;
	rolloverEnabled: boolean;
}

export interface BudgetCurrentPeriod {
	id: string;
	startDate: string;
	endDate: string;
	status: 'Open' | 'Closed';
	allocations: BudgetPeriodCategory[];
}

export interface Budget {
	id: string;
	name: string;
	period: BudgetPeriod;
	currency: string;
	style: BudgetStyle;
	incomeMinor: number;
	isActive: boolean;
	startsOn: string;
	currentPeriod: BudgetCurrentPeriod | null;
}

export interface BudgetReportTotals {
	allocatedMinor: number;
	spentMinor: number;
	remainingMinor: number;
	currency: string;
}

export interface BudgetReportCategoryItem {
	categoryId: string;
	name: string;
	allocatedMinor: number;
	spentMinor: number;
	remainingMinor: number;
	rolloverBalanceMinor: number;
	percentUsed: number;
	currency: string;
}

export interface BudgetReport {
	periodId: string;
	totals: BudgetReportTotals;
	byCategory: BudgetReportCategoryItem[];
	displayCurrency: string;
}

// ── Reconciliations ────────────────────────────────────────────────────────

export type ReconciliationStatus = 'open' | 'completed' | 'aborted';

export interface Reconciliation {
	id: string;
	accountId: string;
	statementDate: string;
	statementBalanceMinor: number;
	currency: string;
	status: ReconciliationStatus;
	note: string | null;
	createdByUserId: string;
	startedAt: string;
	completedAt: string | null;
}

export interface ReconciliationWithTransactions extends Reconciliation {
	includedTransactions: Transaction[];
	diffMinor: number;
}
