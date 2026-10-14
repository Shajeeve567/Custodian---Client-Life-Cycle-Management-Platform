export interface FormatDateOptions {
    withTime?: boolean;
}

const MONTH_NAMES = [
    'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
    'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

/**
 * Format a calendar date without time component:
 * - Today: "Today"
 * - Tomorrow: "Tomorrow"
 * - This year: "Oct 15"
 * - Other year: "Oct 15, 2027"
 */
export function formatDate(iso: string | Date | null | undefined): string {
    if (!iso) return '';
    const date = typeof iso === 'string' ? new Date(iso) : iso;
    if (isNaN(date.getTime())) return '';

    const now = new Date();
    const isSameDay = (d1: Date, d2: Date) =>
        d1.getFullYear() === d2.getFullYear() &&
        d1.getMonth() === d2.getMonth() &&
        d1.getDate() === d2.getDate();

    const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);

    if (isSameDay(date, now)) {
        return 'Today';
    }
    if (isSameDay(date, tomorrow)) {
        return 'Tomorrow';
    }

    const monthStr = MONTH_NAMES[date.getMonth()];
    const day = date.getDate();
    const year = date.getFullYear();

    if (year === now.getFullYear()) {
        return `${monthStr} ${day}`;
    }
    return `${monthStr} ${day}, ${year}`;
}

/**
 * Format a UTC ISO string or Date into human-readable date and time:
 * - Today: "Today, 3:12 PM"
 * - Tomorrow: "Tomorrow, 3:12 PM"
 * - This year: "Oct 15, 3:12 PM"
 * - Other year: "Oct 15, 2027, 3:12 PM"
 *
 * If `options.withTime` is false, returns date-only format without time.
 */
export function formatDateTime(
    iso: string | Date | null | undefined,
    options?: FormatDateOptions
): string {
    if (!iso) return '';
    const date = typeof iso === 'string' ? new Date(iso) : iso;
    if (isNaN(date.getTime())) return '';

    if (options?.withTime === false) {
        return formatDate(date);
    }

    const now = new Date();
    const isSameDay = (d1: Date, d2: Date) =>
        d1.getFullYear() === d2.getFullYear() &&
        d1.getMonth() === d2.getMonth() &&
        d1.getDate() === d2.getDate();

    const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);

    const timeStr = date.toLocaleTimeString('en-US', {
        hour: 'numeric',
        minute: '2-digit',
        hour12: true,
    });

    if (isSameDay(date, now)) {
        return `Today, ${timeStr}`;
    }
    if (isSameDay(date, tomorrow)) {
        return `Tomorrow, ${timeStr}`;
    }

    const monthStr = MONTH_NAMES[date.getMonth()];
    const day = date.getDate();
    const year = date.getFullYear();

    if (year === now.getFullYear()) {
        return `${monthStr} ${day}, ${timeStr}`;
    }
    return `${monthStr} ${day}, ${year}, ${timeStr}`;
}

/**
 * Format duration in minutes into a clean human string:
 * - 30 -> "30 min"
 * - 60 -> "1 hr"
 * - 90 -> "1 hr 30 min"
 */
export function formatDuration(minutes: number | null | undefined): string {
    if (minutes == null || isNaN(minutes) || minutes <= 0) return '';
    const mins = Math.round(minutes);
    const hours = Math.floor(mins / 60);
    const remainingMins = mins % 60;

    if (hours === 0) {
        return `${remainingMins} min`;
    }
    if (remainingMins === 0) {
        return `${hours} hr`;
    }
    return `${hours} hr ${remainingMins} min`;
}
