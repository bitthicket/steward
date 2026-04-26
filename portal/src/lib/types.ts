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
