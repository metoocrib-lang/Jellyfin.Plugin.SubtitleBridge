"""Provider adapters supported by the project.

Only Disney+ has a prototype. Others must provide their own authenticated
navigation and subtitle-track parsing; never assume HLS or a public API.
"""
SUPPORTED_SITES = {
    "disney": {"name": "Disney+", "status": "experimental"},
    "netflix": {"name": "Netflix", "status": "pending"},
    "prime": {"name": "Prime Video", "status": "pending"},
    "max": {"name": "Max", "status": "pending"},
    "apple": {"name": "Apple TV+", "status": "pending"},
}
