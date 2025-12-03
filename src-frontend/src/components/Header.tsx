import React from 'react';
import { useAuth } from '../context/AuthContext';
import './Header.css';

interface HeaderProps {
  onAuthClick: () => void;
  onAboutClick: () => void;
  showAbout: boolean;
}

const Header: React.FC<HeaderProps> = ({ onAuthClick, onAboutClick, showAbout }) => {
  const { user, isAuthenticated, isIncognito, logout, setIncognito } = useAuth();

  return (
    <header className="header">
      <div className="header-left">
        <h1 className="logo" onClick={() => showAbout && onAboutClick()} style={{ cursor: showAbout ? 'pointer' : 'default' }}>
          profiler
        </h1>
        <nav className="header-nav">
          <button
            className={`nav-btn ${!showAbout ? 'active' : ''}`}
            onClick={() => showAbout && onAboutClick()}
          >
            typing test
          </button>
          <button
            className={`nav-btn ${showAbout ? 'active' : ''}`}
            onClick={() => !showAbout && onAboutClick()}
          >
            about
          </button>
        </nav>
      </div>
      <div className="header-right">
        {isIncognito && (
          <span className="incognito-badge">Incognito Mode</span>
        )}
        {isAuthenticated ? (
          <>
            <span className="username">@{user?.username}</span>
            <button className="header-btn" onClick={logout}>
              Logout
            </button>
          </>
        ) : (
          <>
            <button
              className={`header-btn incognito-btn ${isIncognito ? 'active' : ''}`}
              onClick={() => setIncognito(!isIncognito)}
            >
              {isIncognito ? 'Exit Incognito' : 'Go Incognito'}
            </button>
            <button className="header-btn primary" onClick={onAuthClick}>
              Sign In
            </button>
          </>
        )}
      </div>
    </header>
  );
};

export default Header;
