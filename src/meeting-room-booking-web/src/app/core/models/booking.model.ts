/**
 * A booking from the original endpoints, or from an organisation's
 * endpoints, which add what the caller may do with it. A Member gets other
 * people's bookings without title and booked-by (ADR 0006).
 */
export interface Booking {
  readonly id: number;
  readonly roomId: number;
  readonly roomName: string;
  readonly title: string | null;
  readonly bookedBy: string | null;
  readonly startUtc: string;
  readonly endUtc: string;
  readonly createdUtc: string;
  /** Organisation endpoints only: the caller may change and delete it. */
  readonly canModify?: boolean;
  /** Organisation endpoints only: the caller made it. */
  readonly createdByCurrentUser?: boolean;
  /** Organisation endpoints only: title and booked-by are hidden from this caller. */
  readonly detailsHidden?: boolean;
}
