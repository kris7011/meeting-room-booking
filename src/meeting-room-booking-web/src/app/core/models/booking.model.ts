export interface Booking {
  readonly id: number;
  readonly roomId: number;
  readonly roomName: string;
  readonly title: string;
  readonly bookedBy: string;
  readonly startUtc: string;
  readonly endUtc: string;
  readonly createdUtc: string;
}
