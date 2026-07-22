export interface SaveBookingRequest {
  readonly roomId: number;
  readonly title: string;
  readonly bookedBy: string;
  readonly startUtc: string;
  readonly endUtc: string;
}
