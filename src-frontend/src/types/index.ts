export interface KeystrokeEvent {
  key: string;
  timestamp: number;
  type: 'keydown' | 'keyup';
}

export interface SubmitSessionRequest {
  platform: string;
  events: KeystrokeEvent[];
  sessionId?: string;
  userId?: string;
}

export interface User {
  username: string;
  token: string;
}

export interface LoginResponse {
  token: string;
  username: string;
}

export interface IdentifyResponse {
  user: string;
  confidence: number;
  message: string;
  status: string;
  sessionId: string;
}
