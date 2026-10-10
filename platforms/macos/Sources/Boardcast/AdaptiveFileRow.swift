import SwiftUI

/// Chooses a line arrangement from full natural widths before truncation is allowed.
@MainActor
struct AdaptiveFileRow: View {
    var url: URL?
    var name: String
    var isDirectory: Bool
    var metadata: String
    var detail: String

    var body: some View {
        HStack(spacing: 10) {
            FileIcon(url: url, name: name, isDirectory: isDirectory, size: 28)
                .accessibilityHidden(true)
            FileTextLayout {
                Text(verbatim: name).lineLimit(1)
                Text(verbatim: metadata).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 5)
        .help(detail)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(verbatim: name + ", " + metadata))
    }
}

private struct FileTextLayout: Layout {
    private let horizontalGap: CGFloat = 12
    private let verticalGap: CGFloat = 5

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard subviews.count == 2 else { return .zero }
        let name = subviews[0].sizeThatFits(.unspecified)
        let metadata = subviews[1].sizeThatFits(.unspecified)
        let naturalWidth = name.width + horizontalGap + metadata.width
        let width = proposal.width?.isFinite == true ? max(0, proposal.width!) : naturalWidth
        if naturalWidth <= width { return CGSize(width: width, height: max(name.height, metadata.height)) }
        let constrained = ProposedViewSize(width: width, height: nil)
        let first = subviews[0].sizeThatFits(constrained)
        let second = subviews[1].sizeThatFits(constrained)
        return CGSize(width: width, height: first.height + verticalGap + second.height)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard subviews.count == 2 else { return }
        let name = subviews[0].sizeThatFits(.unspecified)
        let metadata = subviews[1].sizeThatFits(.unspecified)
        if name.width + horizontalGap + metadata.width <= bounds.width {
            subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading,
                              proposal: ProposedViewSize(name))
            subviews[1].place(at: CGPoint(x: bounds.maxX, y: bounds.midY), anchor: .trailing,
                              proposal: ProposedViewSize(metadata))
        } else {
            let constrained = ProposedViewSize(width: bounds.width, height: nil)
            let first = subviews[0].sizeThatFits(constrained)
            subviews[0].place(at: bounds.origin, anchor: .topLeading, proposal: constrained)
            subviews[1].place(at: CGPoint(x: bounds.minX, y: bounds.minY + first.height + verticalGap),
                              anchor: .topLeading, proposal: constrained)
        }
    }
}
