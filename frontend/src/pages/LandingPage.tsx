import React from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import {
    Shield,
    ArrowRight,
    FileText,
    Layers,
    AlertTriangle,
    Clock,
    FolderLock,
    ChevronRight
} from 'lucide-react';

export const LandingPage: React.FC = () => {
    const { isAuthenticated, role } = useAuth();

    return (
        <div className="landing-wrapper">
            {/* Luminous atmospheric dot background matching Figma */}
            <div className="landing-dot-bg" aria-hidden="true" />
            <div className="landing-ambient-glow-1" aria-hidden="true" />
            <div className="landing-ambient-glow-2" aria-hidden="true" />

            {/* Navigation Bar */}
            <header className="landing-header">
                <div className="landing-header-inner">
                    <Link to="/" className="landing-brand">
                        <div className="landing-logo-badge">
                            <Shield className="w-5 h-5 text-white" />
                        </div>
                        <span className="landing-brand-title">Custodian</span>
                    </Link>

                    <nav className="landing-nav-links">
                        <a href="#problem">The Problem</a>
                        <a href="#engine">Next Action Engine</a>
                        <a href="#security">Cryptographic Audit</a>
                        {isAuthenticated && role === 'Client' ? (
                            <Link to="/portal">Client Portal</Link>
                        ) : (
                            <Link to="/documents">Document Vault</Link>
                        )}
                    </nav>

                    <div className="landing-cta-group">
                        {isAuthenticated ? (
                            <Link to={role === 'Client' ? '/portal' : '/engagements'} className="landing-primary-btn">
                                <span>{role === 'Client' ? 'Go to Client Portal' : 'Go to Dashboard'}</span>
                                <ArrowRight className="w-4 h-4 ml-1.5" />
                            </Link>
                        ) : (
                            <>
                                <Link to="/login" className="landing-secondary-btn">
                                    Sign In
                                </Link>
                                <Link to="/register" className="landing-primary-btn">
                                    <span>Initialize Workspace</span>
                                    <ArrowRight className="w-4 h-4 ml-1.5" />
                                </Link>
                            </>
                        )}
                    </div>
                </div>
            </header>

            {/* Hero Section */}
            <section className="landing-hero-section">
                <div className="landing-hero-container">
                    <div className="telemetry-pill mb-4">
                        <span className="telemetry-dot" />
                        <span className="telemetry-text">DETERMINISTIC WORKFLOW OS V3.2</span>
                    </div>

                    <h1 className="landing-hero-title">
                        Client onboarding without the chaos.
                        <br />
                        <span className="landing-gradient-text">Enforced with cryptographic certainty.</span>
                    </h1>

                    <p className="landing-hero-subtitle">
                        Custodian eliminates stalled milestones, uncollected assets, and handoff amnesia for digital agencies, product studios, and consultancy teams.
                    </p>

                    <div className="landing-hero-actions">
                        <Link to="/register" className="landing-hero-cta">
                            <span>Initialize Workspace</span>
                            <ArrowRight className="w-4 h-4 ml-2" />
                        </Link>
                        <Link to="/login" className="landing-hero-secondary">
                            <span>Open Existing Console</span>
                            <ChevronRight className="w-4 h-4 ml-1" />
                        </Link>
                    </div>
                </div>
            </section>

            {/* Problem Breakdown Section (Matching Figma Node 5:5) */}
            <section id="problem" className="landing-section bg-slate-50/50 border-y border-slate-200/60">
                <div className="landing-section-inner">
                    <div className="section-header">
                        <span className="telemetry-pill">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">THE OPERATIONAL FRICTION</span>
                        </span>
                        <h2 className="section-title">Winning the client is only the beginning.</h2>
                        <p className="section-description">
                            Traditional agencies hemorrhage margin during the client kickoff phase due to three recurring structural voids:
                        </p>
                    </div>

                    <div className="problem-cards-grid">
                        {/* Problem Card 1 */}
                        <div className="problem-card">
                            <div className="problem-card-icon bg-amber-50 text-amber-600">
                                <Clock className="w-6 h-6" />
                            </div>
                            <h3 className="problem-card-title">The Stalled Approval Void</h3>
                            <p className="problem-card-text">
                                Client leads promise signoff in two days. Eleven business days quietly slip by with no alerts until the project manager frantically discovers the milestone is blown during Monday standup.
                            </p>
                            <div className="problem-card-metric">
                                <span className="text-xs font-semibold text-amber-700">Average Slip: +11.4 Days</span>
                            </div>
                        </div>

                        {/* Problem Card 2 */}
                        <div className="problem-card">
                            <div className="problem-card-icon bg-rose-50 text-rose-600">
                                <FolderLock className="w-6 h-6" />
                            </div>
                            <h3 className="problem-card-title">The Missing Asset Trap</h3>
                            <p className="problem-card-text">
                                Engineering is staffed and ready to build, only to discover raw vector logos, production API tokens, and compliance certificates are scattered across Slack threads or expired links.
                            </p>
                            <div className="problem-card-metric">
                                <span className="text-xs font-semibold text-rose-700">Blocked Sprints: 42%</span>
                            </div>
                        </div>

                        {/* Problem Card 3 */}
                        <div className="problem-card">
                            <div className="problem-card-icon bg-indigo-50 text-indigo-600">
                                <AlertTriangle className="w-6 h-6" />
                            </div>
                            <h3 className="problem-card-title">The Handoff Amnesia</h3>
                            <p className="problem-card-text">
                                Vague boundaries between sales account executives, technical leads, and client ops teams lead everyone to assume someone else is chasing the next required action item.
                            </p>
                            <div className="problem-card-metric">
                                <span className="text-xs font-semibold text-indigo-700">Responsibility Gap: 100% Closed</span>
                            </div>
                        </div>
                    </div>
                </div>
            </section>

            {/* Architecture Pillars Section */}
            <section id="engine" className="landing-section">
                <div className="landing-section-inner">
                    <div className="section-header">
                        <span className="telemetry-pill">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">SYSTEM CAPABILITIES</span>
                        </span>
                        <h2 className="section-title">Built for agency speed with enterprise rigor.</h2>
                        <p className="section-description">
                            Every engagement is governed by state machines with strict gate enforcement and verifiable audit logs.
                        </p>
                    </div>

                    <div className="features-grid">
                        <div className="feature-card">
                            <div className="feature-icon bg-indigo-100 text-indigo-700">
                                <Layers className="w-6 h-6" />
                            </div>
                            <h3 className="feature-title">Next Action Engine</h3>
                            <p className="feature-text">
                                Computes the single pending action blocking an engagement from advancing, assigning clear ownership to either the agency staff lead or client stakeholder.
                            </p>
                            <Link to={role === 'Client' ? '/portal' : '/engagements'} className="feature-link">
                                <span>{role === 'Client' ? 'View Next Action' : 'Explore Engagements'}</span>
                                <ChevronRight className="w-4 h-4" />
                            </Link>
                        </div>

                        <div className="feature-card">
                            <div className="feature-icon bg-purple-100 text-purple-700">
                                <FileText className="w-6 h-6" />
                            </div>
                            <h3 className="feature-title">Encrypted Document Vault</h3>
                            <p className="feature-text">
                                Secure storage for onboarding documentation, contracts, and regulatory certificates, isolated per tenant boundary.
                            </p>
                            <Link to={role === 'Client' ? '/portal' : '/documents'} className="feature-link">
                                <span>{role === 'Client' ? 'Upload Documents' : 'View Document Vault'}</span>
                                <ChevronRight className="w-4 h-4" />
                            </Link>
                        </div>

                        <div className="feature-card">
                            <div className="feature-icon bg-emerald-100 text-emerald-700">
                                <Shield className="w-6 h-6" />
                            </div>
                            <h3 className="feature-title">Genesis Audit Log</h3>
                            <p className="feature-text">
                                Cryptographically chained SHA-256 audit log that permanently records state transitions, signoffs, and verification events.
                            </p>
                            <Link to={role === 'Client' ? '/portal' : '/audit'} className="feature-link">
                                <span>{role === 'Client' ? 'Verification Status' : 'Inspect Audit Chain'}</span>
                                <ChevronRight className="w-4 h-4" />
                            </Link>
                        </div>
                    </div>
                </div>
            </section>

            {/* Footer Matching Figma Node 9:898 */}
            <footer className="landing-footer">
                <div className="landing-footer-inner">
                    <div className="footer-brand-col">
                        <div className="flex items-center gap-2 mb-3">
                            <div className="w-7 h-7 rounded bg-slate-900 flex items-center justify-center">
                                <Shield className="w-4 h-4 text-white" />
                            </div>
                            <span className="font-bold text-slate-900 text-lg">Custodian</span>
                        </div>
                        <p className="text-sm text-slate-500 leading-relaxed mb-4">
                            The deterministic workflow control platform engineered specifically for modern digital agencies, product consultancies, and creative studios.
                        </p>
                        <p className="text-xs text-slate-400">
                            © 2025 Custodian Operating Systems Inc. All rights reserved.
                        </p>
                    </div>

                    <div className="footer-links-col">
                        <h4 className="footer-col-title">PRODUCT</h4>
                        <ul className="footer-links-list">
                            <li><Link to={role === 'Client' ? '/portal' : '/engagements'}>Next Action Engine</Link></li>
                            <li><Link to={role === 'Client' ? '/portal' : '/actions'}>Gate Enforcement</Link></li>
                            <li><Link to="/portal">Client Portal</Link></li>
                            <li><Link to={role === 'Client' ? '/portal' : '/documents'}>Encrypted Vault</Link></li>
                            <li><Link to={role === 'Client' ? '/portal' : '/audit'}>SLA Radar</Link></li>
                        </ul>
                    </div>

                    <div className="footer-links-col">
                        <h4 className="footer-col-title">SOLUTIONS</h4>
                        <ul className="footer-links-list">
                            <li><span>Web & App Studios</span></li>
                            <li><span>Brand Consultancies</span></li>
                            <li><span>Performance Agencies</span></li>
                            <li><span>Enterprise Integrators</span></li>
                        </ul>
                    </div>

                    <div className="footer-links-col">
                        <h4 className="footer-col-title">SECURITY & ACCESS</h4>
                        <ul className="footer-links-list">
                            <li><Link to="/login">Sign In</Link></li>
                            <li><Link to="/register">Create Account</Link></li>
                            <li><Link to="/audit">Genesis Verification</Link></li>
                        </ul>
                    </div>
                </div>
            </footer>
        </div>
    );
};
