import Foundation

/// SwiftPM owns its generated resource bundle; Xcode installs resources in the main app bundle.
enum ResourceBundle {
    static let resources: Bundle = {
        #if SWIFT_PACKAGE
        return .module
        #else
        return .main
        #endif
    }()

    static func url(forResource name: String, withExtension extensionName: String,
                    subdirectory: String? = nil) -> URL? {
        resources.url(forResource: name, withExtension: extensionName, subdirectory: subdirectory)
    }
}
