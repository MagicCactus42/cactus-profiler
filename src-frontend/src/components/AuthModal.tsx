import React, { useState } from 'react';
import { authService } from '../services/api';
import { useAuth } from '../context/AuthContext';
import './AuthModal.css';

interface AuthModalProps {
  mode: 'login' | 'register';
  onClose: () => void;
  onSwitchMode: () => void;
}

const AuthModal: React.FC<AuthModalProps> = ({ mode, onClose, onSwitchMode }) => {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [success, setSuccess] = useState('');
  const { login } = useAuth();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    setLoading(true);

    try {
      if (mode === 'login') {
        const response = await authService.login(username, password);
        login(response.username, response.token);
        onClose();
      } else {
        await authService.register(username, password);
        setSuccess('Registration successful! You can now login.');
        setUsername('');
        setPassword('');
      }
    } catch (err: any) {
      setError(err.response?.data || 'An error occurred');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content" onClick={(e) => e.stopPropagation()}>
        <button className="modal-close" onClick={onClose}>
          &times;
        </button>
        <h2>{mode === 'login' ? 'Login' : 'Register'}</h2>
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label htmlFor="username">Username</label>
            <input
              type="text"
              id="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              autoComplete="username"
            />
          </div>
          <div className="form-group">
            <label htmlFor="password">Password</label>
            <input
              type="password"
              id="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
            />
          </div>
          {error && <div className="error-message">{error}</div>}
          {success && <div className="success-message">{success}</div>}
          <button type="submit" className="submit-btn" disabled={loading}>
            {loading ? 'Loading...' : mode === 'login' ? 'Login' : 'Register'}
          </button>
        </form>
        <p className="switch-mode">
          {mode === 'login' ? (
            <>
              Don't have an account?{' '}
              <button onClick={onSwitchMode}>Register</button>
            </>
          ) : (
            <>
              Already have an account?{' '}
              <button onClick={onSwitchMode}>Login</button>
            </>
          )}
        </p>
      </div>
    </div>
  );
};

export default AuthModal;
