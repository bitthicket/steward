<script lang="ts">
	interface Props {
		amount: number;
		currency: string;
		class?: string;
	}

	let { amount, currency, class: className = '' }: Props = $props();

	function formatMoney(value: number, code: string): string {
		const places = code === 'BTC' ? 8 : 2;
		const absVal = Math.abs(value);
		const formatted = absVal.toLocaleString('en-US', {
			minimumFractionDigits: places,
			maximumFractionDigits: places
		});
		return `${value < 0 ? '-' : ''}${formatted} ${code}`;
	}
</script>

<span class={className}>
	{formatMoney(amount, currency)}
</span>
