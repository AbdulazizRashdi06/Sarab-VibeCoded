# Sarab UX Audit

Scores are current working ratings after the redesign pass, not permanent targets.

| Area | Score | Main issues found | Fixes applied |
| --- | ---: | --- | --- |
| Home / join | 88 | Empty name/code had weak feedback; mobile spacing was tight; join placeholder clipped. | Added local validation, disabled unavailable actions, tightened mobile sizing, fixed nav overlap. |
| Top navigation | 86 | Old header fragments were still styled away; mode icon contrast could disappear; fixed header covered long room screens. | Restyled to match the hand-drawn top bar, improved icon contrast, and made room navigation non-overlapping. |
| Lobby setup | 87 | Players needed stronger phase orientation; settings felt like a generic form; mobile showed roster before setup. | Added global phase banner/progress track, applied sketch-paper controls, and put active game content first on mobile. |
| Answer phase | 88 | Empty answer could be submitted; submitted players still saw the form; phase purpose was not obvious. | Disabled empty submit, added phase guidance, and added a locked waiting state. |
| Self-report phase | 85 | Repeat self-report tap was possible/confusing; claim purpose needed context. | Locked claim button after reporting and added phase guidance. |
| Vote phase | 86 | Confidence and voting were usable but needed stronger page orientation; live room nav consumed space. | Added phase guidance, preserved vote lock behavior, and removed home nav from room play. |
| Results / final scores | 94 | Old results were dense, event-log heavy, final scores only showed the winner, and penalty badges were too vague. | Replaced round results/final scores with one ranked leaderboard system and added exact penalty reason text per player. |
| Game flow automation | 91 | Timers did not advance phases, and all-done answer/vote phases still required host clicks. | Added server-side room clock plus automatic advancement when all active players answer or vote. |
| Admin | 78 | Functional, but still dense and developer-ish. | Applied the same paper/card/control language. |
| Mobile responsiveness | 89 | Bottom nav could overlap content; large hero crowded small screens; room screens started with roster. | Fixed bottom-nav overlap, reduced mobile vertical pressure, removed in-room nav, and reordered active room content first. |

Next priorities:
- Test a full four-player round through every phase on desktop and mobile.
- Continue testing rare jackpot/rollover result states as more prompt packs and game data are added.
- Replace remaining encoded/internal-looking labels with player-facing copy.
