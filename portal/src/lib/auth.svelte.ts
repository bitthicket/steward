import { me } from './api';
import type { User } from './types';

class AuthStore {
	user = $state<User | null>(null);
	loading = $state(true);
	error = $state<string | null>(null);

	constructor() {
		this.refresh();
	}

	async refresh() {
		this.loading = true;
		this.error = null;
		try {
			this.user = await me();
		} catch (e) {
			this.user = null;
			this.error = e instanceof Error ? e.message : 'Unknown error';
		} finally {
			this.loading = false;
		}
	}

	setUser(user: User | null) {
		this.user = user;
	}

	clear() {
		this.user = null;
		this.error = null;
	}
}

export const auth = new AuthStore();
