import axios from 'axios';
import { SubmitSessionRequest, LoginResponse, IdentifyResponse } from '../types';

const API_BASE_URL = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('token');
      localStorage.removeItem('username');
    }
    return Promise.reject(error);
  }
);

export const authService = {
  async login(username: string, password: string): Promise<LoginResponse> {
    const response = await api.post<LoginResponse>('/api/auth/login', {
      username,
      password,
    });
    return response.data;
  },

  async register(username: string, password: string): Promise<{ message: string }> {
    const response = await api.post<{ message: string }>('/api/auth/register', {
      username,
      password,
    });
    return response.data;
  },
};

export const profilerService = {
  async submitSession(data: SubmitSessionRequest): Promise<{ message: string }> {
    const response = await api.post<{ message: string }>('/api/profiler/session', data);
    return response.data;
  },

  async identifyUser(data: SubmitSessionRequest): Promise<IdentifyResponse> {
    const response = await api.post<IdentifyResponse>('/api/profiler/identify', data);
    return response.data;
  },

  async trainModel(): Promise<{ message: string }> {
    const response = await api.post<{ message: string }>('/api/profiler/train');
    return response.data;
  },
};

export default api;
