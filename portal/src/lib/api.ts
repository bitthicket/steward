import type { User, Membership } from './types';

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
