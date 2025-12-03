import React, { useState } from 'react';
import { AuthProvider } from './context/AuthContext';
import Header from './components/Header';
import TypingTest from './components/TypingTest';
import About from './components/About';
import AuthModal from './components/AuthModal';
import './App.css';

function App() {
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [authMode, setAuthMode] = useState<'login' | 'register'>('login');
  const [showAbout, setShowAbout] = useState(false);

  const handleAuthClick = () => {
    setAuthMode('login');
    setShowAuthModal(true);
  };

  const handleSwitchMode = () => {
    setAuthMode((prev) => (prev === 'login' ? 'register' : 'login'));
  };

  const handleAboutClick = () => {
    setShowAbout((prev) => !prev);
  };

  return (
    <AuthProvider>
      <div className="App">
        <Header
          onAuthClick={handleAuthClick}
          onAboutClick={handleAboutClick}
          showAbout={showAbout}
        />
        <main className="main-content">
          {showAbout ? <About /> : <TypingTest />}
        </main>
        {showAuthModal && (
          <AuthModal
            mode={authMode}
            onClose={() => setShowAuthModal(false)}
            onSwitchMode={handleSwitchMode}
          />
        )}
      </div>
    </AuthProvider>
  );
}

export default App;
